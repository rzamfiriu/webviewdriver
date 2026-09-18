// Stand-in for a WKWebView: loads a fixture document in jsdom and evaluates
// incoming scripts against it, replying with the completion value as a string
// (the same contract as WKWebView.evaluateJavaScript). Line-delimited JSON on
// stdio: {id, script} -> {id, ok, result | error}.
'use strict';

const fs = require('fs');
const readline = require('readline');
const { JSDOM } = require('jsdom');

const htmlPath = process.argv[2];
if (!htmlPath) {
  process.stderr.write('usage: node host.js <fixture.html>\n');
  process.exit(2);
}

const dom = new JSDOM(fs.readFileSync(htmlPath, 'utf8'), {
  url: 'https://app.local/index.html',
  runScripts: 'dangerously',
  pretendToBeVisual: true,
});
const window = dom.window;

const rl = readline.createInterface({ input: process.stdin, crlfDelay: Infinity });
rl.on('line', (line) => {
  if (!line.trim()) { return; }
  let message;
  try {
    message = JSON.parse(line);
  } catch {
    return;
  }

  let reply;
  try {
    const result = window.eval(message.script);
    reply = { id: message.id, ok: true, result: result === undefined || result === null ? null : String(result) };
  } catch (e) {
    reply = { id: message.id, ok: false, error: String((e && e.message) || e) };
  }

  process.stdout.write(JSON.stringify(reply) + '\n');
});

rl.on('close', () => process.exit(0));
