const english: Record<string, string> = {
  '打开所在目录':'Open containing folder',
  '等待 Agent':'Waiting for Agent','思考中':'Thinking','正在回复':'Responding','执行工具':'Running tools','等待处理':'Needs attention','压缩上下文':'Compacting context','正在启动':'Starting','已完成':'Completed','正在生成':'Generating',
  '加载更早消息':'Load earlier messages','已到历史保留边界':'End of retained history','完整消息':'Full message','消息内容':'Message','页面状态尚未保存，请保留当前页面并重试':'Page state was not saved. Keep this page open and retry.',
  '预览上限为 8 MiB，请在电脑打开或使用下载':'Preview is limited to 8 MiB. Download the file or open it on the computer.','文件已变化或分块不完整，请重新读取':'The file changed or a chunk is incomplete. Read it again.','此格式暂不支持页面内预览':'This format cannot be previewed here.','HTML 预览':'HTML preview','变更记录已变化，请重新读取':'Changes were updated. Read them again.',
  '仅显示工作目录内的部分文件，可输入名称筛选':'Showing part of the working directory. Filter by filename.','正在连接会话…':'Connecting to conversation…','会话工具':'Conversation tools','⌕ 搜索':'⌕ Search','▱ 文件':'▱ Files','± 变更':'± Changes','用量':'Usage','停止':'Stop',
  '计划':'Plan','项':'items','阅读位置在更早的历史中':'Your reading position is in earlier history','加载并恢复':'Load and restore','当前历史中已不可用':'No longer available in retained history','回到最新':'Go to latest',
  '工作目录文件':'Workspace files','搜索会话':'Search conversation','上下文与用量':'Context and usage','文件变更':'File changes','关闭':'Close','输入要查找的内容':'Find in conversation','查找':'Find','正在搜索…':'Searching…','没有匹配的消息':'No matching messages','加载更多结果':'Load more results','仅搜索电脑当前保留的历史':'Search covers history currently retained on the computer',
  '筛选文件名称':'Filter filenames','筛选':'Filter','保存':'Save','正在读取…':'Reading…','读取完整变更':'Read all changes','变更超过当前保留上限，完整内容请在电脑查看。':'Changes exceed the retained limit. View the full content on the computer.','Agent 尚未报告文件变更':'The Agent has not reported file changes',
  '工具':'Tools','执行中':'running','失败':'failed','查看待处理':'View pending requests','↓ 最新':'↓ Latest','描述要完成的任务':'Describe the task','思考摘要':'Thinking summary','工具操作':'Tool action','你':'You','复制':'Copy','追加到草稿':'Append to draft','引用':'Quote','查看完整内容':'View full content','查看图片':'View image',
  '需要你的回答':'Your answer is needed','需要批准':'Approval needed','请求详情':'Request details','补充回答':'Additional answer','请在电脑端查看完整请求':'Review the full request on the computer','拒绝':'Deny','提交回答':'Submit answer','允许本次':'Allow once',
  'Agent 暂未报告用量':'The Agent has not reported usage','输入':'Input','输出':'Output','缓存命中':'Cached input','累计':'Total','最近一轮':'Latest turn','仅显示 Agent 报告的上下文窗口用量':'Context usage reported by the Agent','Agent 报告费用':'Agent-reported cost','原始用量':'Raw usage','变更摘要':'Change summary','预览':'Preview','源码':'Source','隔离的静态 HTML 预览':'Isolated static HTML preview','代码':'Code',
  '手机连接尚未就绪':'The phone connection is not ready','请等待当前操作完成':'Wait for the current operation to finish','请求结果尚未确认，请检查连接':'The request outcome is unconfirmed. Check the connection.'
};
english['文件已变化或分块不连续，请重新读取']='The file changed or a chunk was incomplete. Read it again.';
let language = 'zh';
export function setLanguage(value?: string) { language = value || 'zh'; document.documentElement.lang = language; }
export function t(value: string) { return language.startsWith('zh') ? value : english[value] || value; }
