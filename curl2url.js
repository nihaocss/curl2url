#!/usr/bin/env node
/**
 * curl2url —— 从 curl 命令里提取请求 URL
 *
 * 典型场景：微信开发者工具 / Chrome 网络面板右键「Copy as cURL」，
 * 复制出来一大段带 header、cookie、body 的命令，这里只保留 URL。
 *
 * 支持：
 * - 一次粘贴多条 curl（批量复制所有请求）
 * - bash `\` / cmd `^` / PowerShell 反引号续行
 * - 单引号 / 双引号 / 无引号 URL
 * - `--url` 参数形式
 * - `&`、`\/` 等 JSON 转义
 * - Referer / Origin 等 header 里的 URL 不会被误提取
 */

'use strict';

const { spawnSync } = require('node:child_process');
const fs = require('node:fs');

const HELP = `用法:
  curl2url [选项] [curl 文本...]

不传文本时默认读取 Windows 剪贴板，提取后把结果写回剪贴板。

选项:
  -f, --file <路径>      从文件读取
  -s, --stdin            从标准输入读取
  -k, --keep-duplicates  保留重复 URL（默认去重）
  -1, --first            只输出第一个 URL
  -n, --no-clipboard     不写回剪贴板
  -h, --help             显示帮助

示例:
  curl2url                                 剪贴板 -> 剪贴板
  curl2url -f curl.txt                     文件 -> 剪贴板
  Get-Clipboard | curl2url -s -n           标准输入，只打印
  curl2url "curl 'https://a.com/x?a=1' -H 'x: y'"
`;

/**
 * 解析命令行参数。
 * @param {string[]} argv process.argv.slice(2)
 * @returns {object} 选项对象
 */
function parseArgs(argv) {
  const opts = {
    file: null,
    stdin: false,
    text: null,
    keepDuplicates: false,
    first: false,
    writeClipboard: true,
    help: false,
  };
  const rest = [];

  for (let i = 0; i < argv.length; i++) {
    const arg = argv[i];
    switch (arg) {
      case '-h':
      case '--help':
        opts.help = true;
        break;
      case '-f':
      case '--file':
        opts.file = argv[++i];
        if (!opts.file) throw new Error('--file 需要指定路径');
        break;
      case '-s':
      case '--stdin':
        opts.stdin = true;
        break;
      case '-k':
      case '--keep-duplicates':
        opts.keepDuplicates = true;
        break;
      case '-1':
      case '--first':
        opts.first = true;
        break;
      case '-n':
      case '--no-clipboard':
        opts.writeClipboard = false;
        break;
      default:
        if (arg.startsWith('-') && arg !== '-') {
          throw new Error(`未知选项: ${arg}`);
        }
        rest.push(arg);
    }
  }

  if (rest.length > 0) opts.text = rest.join(' ');
  return opts;
}

/**
 * 读取 Windows 剪贴板文本。
 * @returns {string}
 */
function readClipboard() {
  const result = spawnSync(
    'powershell.exe',
    [
      '-NoProfile',
      '-NonInteractive',
      '-Command',
      '[Console]::OutputEncoding=[Text.Encoding]::UTF8; Get-Clipboard -Raw',
    ],
    { encoding: 'utf8', windowsHide: true, maxBuffer: 64 * 1024 * 1024 }
  );

  if (result.error) throw new Error(`读取剪贴板失败: ${result.error.message}`);
  if (result.status !== 0) {
    throw new Error(`读取剪贴板失败: ${(result.stderr || '').trim() || 'Get-Clipboard 返回非 0'}`);
  }
  return result.stdout || '';
}

/**
 * 把文本写入 Windows 剪贴板。
 * @param {string} text
 */
function writeClipboard(text) {
  const script =
    '[Console]::InputEncoding=[Text.Encoding]::UTF8; ' +
    '$text=[Console]::In.ReadToEnd(); ' +
    'Set-Clipboard -Value $text';

  const result = spawnSync(
    'powershell.exe',
    ['-NoProfile', '-NonInteractive', '-Command', script],
    { input: text, encoding: 'utf8', windowsHide: true, maxBuffer: 64 * 1024 * 1024 }
  );

  if (result.error) throw new Error(`写入剪贴板失败: ${result.error.message}`);
  if (result.status !== 0) {
    throw new Error(`写入剪贴板失败: ${(result.stderr || '').trim() || 'Set-Clipboard 返回非 0'}`);
  }
}

/**
 * 统一常见转义和续行，方便后续分词。
 * @param {string} text
 * @returns {string}
 */
function normalize(text) {
  return text
    .replace(/^\\uFEFF/, '')
    .replace(/\\u0026/gi, '&')
    .replace(/\\u003d/gi, '=')
    .replace(/\\\//g, '/')
    .replace(/&amp;/g, '&')
    .replace(/\\\r?\n/g, ' ') // bash 续行: \ + 换行
    .replace(/\^\r?\n/g, ' ') // cmd 续行: ^ + 换行
    .replace(/`\r?\n/g, ' '); // PowerShell 续行: ` + 换行
}

/**
 * 按 shell 规则粗分词：引号内的空格不拆，引号本身去掉。
 * @param {string} block
 * @returns {string[]}
 */
function splitTokens(block) {
  const tokens = [];
  let current = '';
  let quote = null;

  for (let i = 0; i < block.length; i++) {
    const ch = block[i];

    if (quote) {
      if (ch === quote) {
        quote = null;
        continue;
      }
      // 双引号内的反斜杠转义
      if (ch === '\\' && quote === '"' && i + 1 < block.length) {
        current += block[++i];
        continue;
      }
      current += ch;
      continue;
    }

    if (ch === '"' || ch === "'") {
      quote = ch;
      continue;
    }

    if (/\s/.test(ch)) {
      if (current) {
        tokens.push(current);
        current = '';
      }
      continue;
    }

    current += ch;
  }

  if (current) tokens.push(current);
  return tokens;
}

/**
 * 判断一个 token 是否是 http(s) URL。
 * @param {string} token
 * @returns {boolean}
 */
function isHttpUrl(token) {
  return /^https?:\/\/[^\s]+$/i.test(token);
}

/**
 * 从单条 curl 命令中挑出目标 URL。
 * 优先级：--url 参数 > curl 之后的第一个 http(s) token。
 * @param {string} block
 * @returns {string|null}
 */
function pickUrlFromBlock(block) {
  const tokens = splitTokens(block);

  const urlFlagIndex = tokens.findIndex(
    (token) => token === '--url' || token === '-url'
  );
  if (urlFlagIndex >= 0 && tokens[urlFlagIndex + 1] && isHttpUrl(tokens[urlFlagIndex + 1])) {
    return tokens[urlFlagIndex + 1];
  }

  for (let i = 1; i < tokens.length; i++) {
    if (isHttpUrl(tokens[i])) return tokens[i];
  }

  return null;
}

/**
 * 提取文本中所有 curl 请求的 URL。
 * 没有 curl 关键字时，退化为提取文本里出现的 URL。
 * @param {string} text
 * @returns {string[]}
 */
function extractUrls(text) {
  const source = normalize(text);
  const curlPattern = /\bcurl(?:\.exe)?\b/gi;
  const starts = [];
  let match;

  while ((match = curlPattern.exec(source)) !== null) {
    starts.push(match.index);
  }

  // 不是 curl 文本，就按普通 URL 列表处理
  if (starts.length === 0) {
    return source.match(/https?:\/\/[^\s'"<>\\)]+/gi) || [];
  }

  const urls = [];
  for (let i = 0; i < starts.length; i++) {
    const end = i + 1 < starts.length ? starts[i + 1] : source.length;
    const block = source.slice(starts[i], end);
    const url = pickUrlFromBlock(block);
    if (url) urls.push(url);
  }
  return urls;
}

/**
 * 去重，保留首次出现的顺序。
 * @param {string[]} urls
 * @returns {string[]}
 */
function unique(urls) {
  const seen = new Set();
  const result = [];
  for (const url of urls) {
    if (!seen.has(url)) {
      seen.add(url);
      result.push(url);
    }
  }
  return result;
}

/**
 * 读输入：文本参数 > 文件 > 标准输入 > 剪贴板。
 * @param {object} opts
 * @returns {{ text: string, fromClipboard: boolean }}
 */
function readInput(opts) {
  if (opts.text !== null) {
    return { text: opts.text, fromClipboard: false };
  }
  if (opts.file) {
    return { text: fs.readFileSync(opts.file, 'utf8'), fromClipboard: false };
  }
  if (opts.stdin) {
    return { text: fs.readFileSync(0, 'utf8'), fromClipboard: false };
  }
  return { text: readClipboard(), fromClipboard: true };
}

function main() {
  let opts;
  try {
    opts = parseArgs(process.argv.slice(2));
  } catch (error) {
    process.stderr.write(`${error.message}\n\n${HELP}`);
    process.exit(2);
  }

  if (opts.help) {
    process.stdout.write(HELP);
    return;
  }

  let input;
  try {
    input = readInput(opts);
  } catch (error) {
    process.stderr.write(`${error.message}\n`);
    process.exit(1);
  }

  let urls = extractUrls(input.text);

  if (!opts.keepDuplicates) urls = unique(urls);
  if (opts.first && urls.length > 1) urls = urls.slice(0, 1);

  if (urls.length === 0) {
    process.stderr.write('没有找到 URL。请确认剪贴板里是 curl 命令。\n');
    process.exit(1);
  }

  const output = urls.join('\n');
  process.stdout.write(`${output}\n`);

  // 从剪贴板来的内容，默认把结果写回剪贴板，形成「复制 curl -> 粘贴 URL」
  if (input.fromClipboard && opts.writeClipboard) {
    try {
      writeClipboard(output);
      process.stderr.write(`已复制 ${urls.length} 个 URL 到剪贴板\n`);
    } catch (error) {
      process.stderr.write(`${error.message}\n`);
      process.exit(1);
    }
  }
}

main();
