// Local scripted OpenAI-compatible endpoint for an end-to-end smoke run. Not a real model.
const http = require('http');
const log = [];
function send(res, obj) { res.writeHead(200, { 'content-type': 'application/json' }); res.end(JSON.stringify(obj)); }
function chat(model, message) {
  return { id: 'c' + Date.now(), object: 'chat.completion', created: Math.floor(Date.now() / 1000), model,
    choices: [{ index: 0, message, finish_reason: message.tool_calls ? 'tool_calls' : 'stop' }],
    usage: { prompt_tokens: 1, completion_tokens: 1, total_tokens: 2 } };
}
function sse(res, model, delta, finish) {
  res.writeHead(200, { 'content-type': 'text/event-stream' });
  const base = { id: 's' + Date.now(), object: 'chat.completion.chunk', created: Math.floor(Date.now() / 1000), model };
  res.write('data: ' + JSON.stringify({ ...base, choices: [{ index: 0, delta, finish_reason: null }] }) + '\n\n');
  res.write('data: ' + JSON.stringify({ ...base, choices: [{ index: 0, delta: {}, finish_reason: finish }],
    usage: { prompt_tokens: 1, completion_tokens: 1, total_tokens: 2 } }) + '\n\n');
  res.end('data: [DONE]\n\n');
}
const text = c => typeof c === 'string' ? c : Array.isArray(c) ? c.map(p => p.text || '').join('') : '';
http.createServer((req, res) => {
  let body = '';
  req.on('data', d => body += d);
  req.on('end', () => {
    if (req.url.endsWith('/models')) return send(res, { object: 'list', data: ['pca', 'reviewer', 'coder'].map(id => ({ id, object: 'model' })) });
    const r = JSON.parse(body || '{}');
    const msgs = r.messages || [];
    const toolResults = msgs.filter(m => m.role === 'tool');
    log.push(`${r.model} stream=${!!r.stream} messages=${msgs.length} tools=${(r.tools || []).length} toolResults=${toolResults.length}`);
    console.error(log[log.length - 1]);
    if (r.model === 'pca') {
      // Wrapped like reasoning models often reply; MAF strips <think> and the code fence.
      return send(res, chat(r.model, { role: 'assistant', content: '<think>plan the task</think>\n```json\n' + JSON.stringify({
        plan: 'Read src/Calculator.cs and report what Add returns, with line numbers.',
        focusFiles: ['src/Calculator.cs'], risks: ['Single small file; little context.'] }) + '\n```' }));
    }
    if (r.model === 'reviewer') {
      const user = text(msgs.filter(m => m.role === 'user').pop().content);
      const data = JSON.parse(user.slice(user.indexOf('Untrusted task data: ') + 21));
      if (toolResults.length === 0)
        return send(res, chat(r.model, { role: 'assistant', content: null, tool_calls: data.Sources.map((s, i) => ({ id: 'call_r' + i, type: 'function',
          function: { name: 'ReadEvidence', arguments: JSON.stringify({ reference: s.Reference, startLine: s.StartLine, endLine: s.EndLine }) } })) }));
      return send(res, chat(r.model, { role: 'assistant', content: JSON.stringify({ status: 'SufficientEvidence',
        summary: 'Observed source lines confirm Add returns a + b, matching the delegated summary.',
        evidenceReferences: data.Sources.map(s => s.Reference), limitations: [] }) }));
    }
    // OpenCode's own model (streaming): first read the file, then answer.
    const isTitle = msgs.some(m => m.role === 'system' && /title/i.test(text(m.content))) && !(r.tools || []).length;
    if (isTitle) return r.stream ? sse(res, r.model, { role: 'assistant', content: 'Calculator Add' }, 'stop')
      : send(res, chat(r.model, { role: 'assistant', content: 'Calculator Add' }));
    if (toolResults.length === 0 && (r.tools || []).some(t => t.function.name === 'read'))
      return sse(res, r.model, { role: 'assistant', tool_calls: [{ index: 0, id: 'call_read1', type: 'function',
        function: { name: 'read', arguments: JSON.stringify({ filePath: process.env.FIXTURE + '/src/Calculator.cs' }) } }] }, 'tool_calls');
    return sse(res, r.model, { role: 'assistant', content: 'src/Calculator.cs: Add(int a, int b) returns a + b (lines 1-4).' }, 'stop');
  });
}).listen(4100, '127.0.0.1', () => console.error('fake llm on 4100'));
