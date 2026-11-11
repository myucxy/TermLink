// SPDX-License-Identifier: GPL-3.0-only
using System.Text.Json;
using TermLink.Agent;

internal static class AiStructuredInteractionsValidation
{
    internal static void Run()
    {
        var form = J("""{"mode":"form","requestedSchema":{"type":"object","properties":{"title":{"type":"string","minLength":2},"note":{"type":"string"},"count":{"type":"integer","minimum":1,"maximum":5},"enabled":{"type":"boolean"},"choice":{"type":"string","enum":["a","b"]}},"required":["title","count","enabled","choice"]}}""");
        Require(AiStructuredInteractions.CanAnswer(AiStructuredInteractions.Elicitation,form),"Supported form rejected");
        Require(!AiStructuredInteractions.Questions(AiStructuredInteractions.Elicitation,form).Single(q=>q.Id=="note").Required,"Optional field required");
        var answers=new Dictionary<string,string[]>{{"title",["中文"]},{"note",[]},{"count",["3"]},{"enabled",["false"]},{"choice",["b"]}};
        var result=JsonSerializer.SerializeToElement(AiStructuredInteractions.Response(AiStructuredInteractions.Elicitation,form,true,answers));
        var content=result.GetProperty("content");
        Require(content.GetProperty("count").GetInt32()==3&&!content.GetProperty("enabled").GetBoolean()&&!content.TryGetProperty("note",out _),"Typed/optional form response invalid");
        answers["count"]=["999"];Reject(()=>AiStructuredInteractions.Response(AiStructuredInteractions.Elicitation,form,true,answers));
        answers["count"]=["3"];answers["extra"]=["injected"];Reject(()=>AiStructuredInteractions.Response(AiStructuredInteractions.Elicitation,form,true,answers));
        foreach(var invalid in new[]{"""{"mode":"url","url":"https://example.invalid"}""","""{"mode":"form","requestedSchema":{"type":"object","properties":{"x":{"type":"string","pattern":"a+"}}}}""","""{"mode":"form","requestedSchema":{"type":"object","properties":{"x":{"type":"object"}}}}"""})
            Require(!AiStructuredInteractions.CanAnswer(AiStructuredInteractions.Elicitation,J(invalid)),"Unsupported schema accepted");
        answers.Remove("extra");answers["note"]=null!;Reject(()=>AiStructuredInteractions.Response(AiStructuredInteractions.Elicitation,form,true,answers));
        var huge=JsonSerializer.SerializeToElement(new { mode="form", requestedSchema=new { type="object",properties=new { text=new {type="string",description=new string('中',9000)} } } });
        Require(AiStructuredInteractions.Questions(AiStructuredInteractions.Elicitation,huge).Length==0&&!AiStructuredInteractions.CanAnswer(AiStructuredInteractions.Elicitation,huge),"Oversized form projected questions");
        var permission=J("""{"permissions":{"network":{"enabled":true},"fileSystem":{"read":["/project"],"write":null}}}""");
        Reject(()=>AiStructuredInteractions.Response(AiStructuredInteractions.Permissions,permission,true,new(){{"network",null!}}));
        var grant=JsonSerializer.SerializeToElement(AiStructuredInteractions.Response(AiStructuredInteractions.Permissions,permission,true,new(){{"network",["拒绝"]},{"fileSystem",["允许"]}}));
        Require(grant.GetProperty("scope").GetString()=="turn"&&!grant.GetProperty("permissions").TryGetProperty("network",out _)&&grant.GetProperty("permissions").GetProperty("fileSystem").GetProperty("read")[0].GetString()=="/project","Permission subset broadened");
        var deny=JsonSerializer.SerializeToElement(AiStructuredInteractions.Response(AiStructuredInteractions.Permissions,permission,false,null!));
        Require(!deny.GetProperty("permissions").EnumerateObject().Any(),"Denied request granted permissions");
        Console.WriteLine("PASS structured interactions: optional typed MCP forms, constraints/injection rejection, permission subsets scoped to turn");
    }
    private static JsonElement J(string value)=>JsonDocument.Parse(value).RootElement.Clone();
    private static void Require(bool value,string message){if(!value)throw new InvalidOperationException(message);}
    private static void Reject(Action action){try{action();}catch(AiOperationException){return;}throw new InvalidOperationException("Invalid answer accepted");}
}
