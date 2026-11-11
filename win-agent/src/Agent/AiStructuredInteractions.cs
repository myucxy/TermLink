// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text;

namespace TermLink.Agent;

// Pure, bounded projections; responses are built from the original pending
// schema so the phone can never add an unrequested permission or property.
internal static class AiStructuredInteractions
{
    internal const string Elicitation = "mcpServer/elicitation/request";
    internal const string Permissions = "item/permissions/requestApproval";
    internal static bool CanAnswer(string method, JsonElement parameters)
    {
        if (parameters.GetRawText().Length > 8000 || JsonSerializer.SerializeToUtf8Bytes(parameters).Length > 16000) return false;
        if (method == Permissions) return PermissionsValid(parameters);
        if (method != Elicitation) return true;
        return TryForm(parameters, out _);
    }
    internal static AiAgentSession.Question[] Questions(string method, JsonElement p)
    {
        if (p.GetRawText().Length > 8000 || JsonSerializer.SerializeToUtf8Bytes(p).Length > 16000) return Array.Empty<AiAgentSession.Question>();
        if (method == Permissions && PermissionsValid(p)) {
            var permissions = AiAgentSession.P(p,"permissions");
            return permissions.EnumerateObject().Where(v=>v.Value.ValueKind == JsonValueKind.Object).Select(v=>
                new AiAgentSession.Question(v.Name, v.Name == "network" ? "允许请求中的网络权限（仅本轮）？" : "允许请求中的文件权限（仅本轮）？", new[]{"允许","拒绝"},false)).ToArray();
        }
        if (method != Elicitation || !TryForm(p,out var properties)) return Array.Empty<AiAgentSession.Question>();
        var required=AiAgentSession.P(AiAgentSession.P(p,"requestedSchema"),"required");
        var names=required.ValueKind==JsonValueKind.Array ? required.EnumerateArray().Select(v=>v.GetString()).ToHashSet(StringComparer.Ordinal):new HashSet<string>();
        return properties.Select(property=>{
            var options=AiAgentSession.P(property.Value,"enum");
            var type=AiAgentSession.S(property.Value,"type");
            var values=type=="boolean" ? new[]{"true","false"}:options.ValueKind==JsonValueKind.Array ? options.EnumerateArray().Select(v=>v.GetString()).ToArray():Array.Empty<string>();
            var label=AiAgentSession.S(property.Value,"title");if(label.Length==0)label=property.Name;
            return new AiAgentSession.Question(property.Name,label,values,false,names.Contains(property.Name));
        }).ToArray();
    }
    internal static object Response(string method,JsonElement p,bool allow,Dictionary<string,string[]> answers)
    {
        if (answers != null && answers.Any(pair => pair.Value == null || pair.Value.Any(v => v == null))) throw new AiOperationException("INVALID_ANSWER");
        if (allow && !CanAnswer(method,p)) throw new AiOperationException("INTERACTION_UNSUPPORTED");
        if (method == Permissions) {
            var grants=new Dictionary<string,JsonElement>();
            if (allow) foreach(var pair in AiAgentSession.P(p,"permissions").EnumerateObject()) {
                if(pair.Value.ValueKind!=JsonValueKind.Object)continue;
                if(answers==null||!answers.TryGetValue(pair.Name,out var choice)||choice.Length!=1||choice[0] is not ("允许" or "拒绝"))throw new AiOperationException("ANSWER_REQUIRED");
                if(choice[0]=="允许")grants[pair.Name]=pair.Value.Clone();
            }
            return new {permissions=grants,scope="turn"};
        }
        var content=new Dictionary<string,object>();
        if(allow){
            TryForm(p,out var properties);
            var questions=Questions(method,p);
            if(answers!=null&&answers.Keys.Any(key=>!properties.Any(v=>v.Name==key)))throw new AiOperationException("INVALID_ANSWER");
            foreach(var property in properties){
                var values=answers!=null&&answers.TryGetValue(property.Name,out var supplied)?supplied:Array.Empty<string>();
                if(values.Length==0||values.All(string.IsNullOrWhiteSpace)){
                    if(questions.First(q=>q.Id==property.Name).Required)throw new AiOperationException("ANSWER_REQUIRED");
                    continue;
                }
                if(values.Length!=1||values[0].Length>4000)throw new AiOperationException("INVALID_ANSWER");
                var text=values[0];var schema=property.Value;var type=AiAgentSession.S(schema,"type");
                object value=text;
                if(type=="boolean"){if(text is not ("true" or "false"))throw new AiOperationException("INVALID_ANSWER");value=text=="true";}
                else if(type is "number" or "integer"){
                    if(!decimal.TryParse(text,NumberStyles.Float,CultureInfo.InvariantCulture,out var number)||type=="integer"&&decimal.Truncate(number)!=number)throw new AiOperationException("INVALID_ANSWER");
                    if(AiAgentSession.P(schema,"minimum").TryDecimal(out var min)&&number<min||AiAgentSession.P(schema,"maximum").TryDecimal(out var max)&&number>max)throw new AiOperationException("INVALID_ANSWER");
                    value=number;
                }else{
                    var scalarCount=text.EnumerateRunes().Count();
                    if(AiAgentSession.P(schema,"minLength").TryDecimal(out var min)&&scalarCount<min||AiAgentSession.P(schema,"maxLength").TryDecimal(out var max)&&scalarCount>max)throw new AiOperationException("INVALID_ANSWER");
                    var options=AiAgentSession.P(schema,"enum");if(options.ValueKind==JsonValueKind.Array&&!options.EnumerateArray().Any(v=>v.GetString()==text))throw new AiOperationException("INVALID_ANSWER");
                }
                content[property.Name]=value;
            }
        }
        return new {action=allow?"accept":"decline",content=allow?(object)content:null,_meta=(object)null};
    }
    private static bool PermissionsValid(JsonElement p)
    {
        var value=AiAgentSession.P(p,"permissions");
        if(value.ValueKind!=JsonValueKind.Object)return false;
        foreach(var pair in value.EnumerateObject()){
            if(pair.Name is not ("network" or "fileSystem"))return false;
            if(pair.Value.ValueKind is not (JsonValueKind.Object or JsonValueKind.Null))return false;
        }
        return true;
    }
    private static bool TryForm(JsonElement p,out JsonProperty[] properties)
    {
        properties=Array.Empty<JsonProperty>();if(AiAgentSession.S(p,"mode")!="form")return false;
        var schema=AiAgentSession.P(p,"requestedSchema");if(AiAgentSession.S(schema,"type")!="object")return false;
        if(schema.EnumerateObject().Any(v=>v.Name is not ("type" or "properties" or "required" or "title" or "description" or "additionalProperties")))return false;
        var additional=AiAgentSession.P(schema,"additionalProperties");if(additional.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.False))return false;
        var raw=AiAgentSession.P(schema,"properties");if(raw.ValueKind!=JsonValueKind.Object)return false;
        properties=raw.EnumerateObject().ToArray();if(properties.Length>16||properties.Select(v=>v.Name).Distinct(StringComparer.Ordinal).Count()!=properties.Length)return false;
        var allowed=new[]{"type","title","description","enum","enumNames","default","minimum","maximum","minLength","maxLength"}.ToHashSet();
        foreach(var property in properties){
            if(property.Name.Length is <1 or >100||property.Name.Any(char.IsControl)||property.Value.ValueKind!=JsonValueKind.Object)return false;
            if(property.Value.EnumerateObject().Any(v=>!allowed.Contains(v.Name)))return false; // unsupported constraints never silently ignored
            if(AiAgentSession.S(property.Value,"type") is not ("string" or "number" or "integer" or "boolean"))return false;
            if(AiAgentSession.S(property.Value,"title").Length>1000)return false;
            var options=AiAgentSession.P(property.Value,"enum");
            if(options.ValueKind!=JsonValueKind.Undefined&&(AiAgentSession.S(property.Value,"type")!="string"||options.ValueKind!=JsonValueKind.Array||options.GetArrayLength()>32||options.EnumerateArray().Any(v=>v.ValueKind!=JsonValueKind.String||v.GetString().Length>1000)))return false;
            foreach(var constraint in new[]{"minimum","maximum","minLength","maxLength"}){
                var value=AiAgentSession.P(property.Value,constraint);if(value.ValueKind!=JsonValueKind.Undefined&&!value.TryDecimal(out _))return false;
            }
        }
        var required=AiAgentSession.P(schema,"required");
        return required.ValueKind==JsonValueKind.Undefined||required.ValueKind==JsonValueKind.Array&&required.GetArrayLength()<=16&&required.EnumerateArray().All(v=>v.ValueKind==JsonValueKind.String&&raw.TryGetProperty(v.GetString(),out _));
    }
    private static bool TryDecimal(this JsonElement value,out decimal number){number=0;return value.ValueKind==JsonValueKind.Number&&value.TryGetDecimal(out number);}
}
