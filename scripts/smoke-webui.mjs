// 控制台前端的转义与注入面回归测试。
//
//   node scripts/smoke-webui.mjs
//
// 这不是"照着实现再抄一份断言"的自欺欺人：脚本把真正的 wwwroot/app.js
// 放进一个 Node vm 里跑起来，拿到里面真实的 escapeHtml / escapeAttr 再断言。
// 所以它测的就是浏览器会执行的那份代码。
//
// 背景：教室名和老师显示名都能由匿名接口写入。之前这两个值被拼进内联事件属性
// （onclick 那类），转义函数又漏了反斜杠，构造 \');alert(1)// 就能让输出变成
// \\');alert(1)// —— 在 JS 里 '\\' 是合法的单反斜杠字符串，后面的代码就自由了，
// 能直接读走 sessionStorage 里的管理员令牌。
//
// 现在数据只进 data-* 属性，只需要一层 HTML 属性转义。这个脚本守两件事：
//   1. escapeHtml 对任何输入都不产出能跳出属性引号或标签的字符；
//   2. 代码里不再有任何内联事件属性可供注入。
//
// 退出码 0 表示通过。

import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';
import vm from 'node:vm';

const here = dirname(fileURLToPath(import.meta.url));
const appJsPath = join(here, '..', 'src', 'ClassShout.RelayServer', 'wwwroot', 'app.js');
const source = readFileSync(appJsPath, 'utf8');

let failures = 0;

function check(name, ok, detail) {
  if (ok) {
    console.log(`  [通过] ${name}${detail ? ' —— ' + detail : ''}`);
  } else {
    console.log(`  [失败] ${name}${detail ? ' —— ' + detail : ''}`);
    failures++;
  }
}

/* ---------- 1. 把真正的 app.js 跑起来，取出真实的转义函数 ---------- */

// 页面里 boot() 会碰 document / sessionStorage，这里给最小桩件，
// 让它跑完不报错。桩件不参与断言，只为把模块顶层执行过去。
const stubElement = { addEventListener() { }, focus() { }, classList: { add() { }, remove() { }, toggle() { } } };
const sandbox = {
  document: {
    addEventListener() { },
    getElementById: () => stubElement,
    querySelectorAll: () => [],
    createElement: () => stubElement,
    body: { appendChild() { } },
  },
  sessionStorage: { getItem: () => null, setItem() { }, removeItem() { } },
  setTimeout,
  console,
};

vm.createContext(sandbox);

// 在同一段脚本作用域里追加一行，把两个函数挂到全局上 ——
// const 声明不会成为 vm 上下文的属性，函数声明会，所以这样最稳。
vm.runInContext(source + '\n;globalThis.__probe = { escapeHtml, escapeAttr };', sandbox);

const { escapeHtml, escapeAttr } = sandbox.__probe;
if (typeof escapeHtml !== 'function' || typeof escapeAttr !== 'function') {
  console.error('无法从 app.js 里取到 escapeHtml / escapeAttr，测试本身有问题。');
  process.exit(1);
}

/* ---------- 2. 转义函数的行为 ---------- */

console.log('转义函数行为');

// 第 1 条是审计里那条真实攻击载荷，其余是各种跳出姿势
const payloads = [
  ["\\');alert(1);//", '审计里的反斜杠载荷'],
  ["</script><script>alert(1)</script>", '闭合 script'],
  ['" onmouseover="alert(1)', '闭合双引号属性'],
  ["' onmouseover='alert(1)", '闭合单引号属性'],
  ['--><script>alert(1)</script>', '闭合注释'],
  ['<img src=x onerror=alert(1)>', '标签注入'],
  ['javascript:alert(1)', '伪协议'],
  ['&lt;script&gt;', '已转义的实体'],
  ['三年二班', '正常中文名'],
  ['a\\', '以反斜杠结尾'],
  ['', '空串'],
  [null, 'null'],
  [undefined, 'undefined'],
];

const forbidden = ["'", '"', '<', '>'];
let allSafe = true;
const offenders = [];

for (const [payload, label] of payloads) {
  const out = escapeHtml(payload);
  const bad = forbidden.filter(ch => out.includes(ch));
  if (bad.length > 0) {
    allSafe = false;
    offenders.push(`${label}: 输出仍含 ${bad.join(' ')} -> ${out}`);
  }
  if (typeof out !== 'string') {
    allSafe = false;
    offenders.push(`${label}: 返回值不是字符串`);
  }
}

check('任何输入都不会产出可跳出属性的字符', allSafe,
  allSafe ? `已覆盖 ${payloads.length} 种载荷` : offenders.join(' | '));

// & 只能以实体的形式出现，否则会二次解析
const entityPattern = /^(&(amp|lt|gt|quot|#39);|[^&])*$/;
let ampSafe = true;
for (const [payload, label] of payloads) {
  if (!entityPattern.test(escapeHtml(payload))) {
    ampSafe = false;
    offenders.push(`${label}: & 未全部转成实体`);
  }
}
check('& 一律转成实体（避免二次解析）', ampSafe);

// 单独把审计里那条载荷的输出打出来，便于人工核对
console.log(`      载荷 \\');alert(1);//  ->  ${JSON.stringify(escapeHtml("\\');alert(1);//"))}`);

// escapeAttr 必须与 escapeHtml 同源，不能是另一套更弱的规则
check('escapeAttr 与 escapeHtml 行为一致',
  payloads.every(([p]) => escapeAttr(p) === escapeHtml(p)));

/* ---------- 3. 注入面：不再有内联事件属性 ---------- */

console.log('');
console.log('注入面');

const inlineHandlerPattern = /\son[a-z]+\s*=/gi;
const codeOnly = source.replace(/\/\*[\s\S]*?\*\//g, '').replace(/^[ \t]*\/\/.*$/gm, '');
const handlers = codeOnly.match(inlineHandlerPattern) ?? [];

check('代码里没有内联事件属性', handlers.length === 0,
  handlers.length === 0 ? '数据只进 data-* 属性' : `发现 ${handlers.join(', ')}`);

// 每处 data-* 绑定的值表达式：取 "+ <expr> +" 中间那一段
const dataBindings = [...codeOnly.matchAll(/data-[a-z-]+="'\s*\+\s*([^+]*?)\s*\+/g)]
  .map(m => m[1].trim());

// 布尔值不需要转义 —— 它由 JS 的 true/false 拼出来，不是插值文本。
// 形如 (!u.disabled) 的表达式放行，其余一律要求走 escapeAttr。
const isSafeBinding = expr => expr.startsWith('escapeAttr(') || /^\(!?\w[\w.]*\)$/.test(expr);
const unsafeBindings = dataBindings.filter(e => !isSafeBinding(e));

check('data-* 属性值一律转义后才拼接',
  dataBindings.length > 0 && unsafeBindings.length === 0,
  unsafeBindings.length === 0
    ? `共 ${dataBindings.length} 处绑定`
    : `未转义：${unsafeBindings.join(' | ')}`);

/* ---------- 4. 每个 data-action 都要有处理函数 ---------- */

console.log('');
console.log('动作接线');

// 从 actions 对象里取键。它是唯一的分发表，写错一个键就是一个点不动的死按钮 ——
// 而"点了没反应"在浏览器里不会有任何报错，只能靠这种结构性检查发现。
const actionsBlock = codeOnly.match(/const actions\s*=\s*\{([\s\S]*?)\n\};/);
const actionHandlers = new Set(
  (actionsBlock ? actionsBlock[1].matchAll(/^\s*'?([a-z][a-z-]*)'?\s*:/gm) : []).map(m => m[1]));

const usedInJs = new Set([...codeOnly.matchAll(/data-action="([a-z-]+)"/g)].map(m => m[1]));

const htmlPath = join(here, '..', 'src', 'ClassShout.RelayServer', 'wwwroot', 'index.html');
const htmlSource = readFileSync(htmlPath, 'utf8');
const usedInHtml = new Set([...htmlSource.matchAll(/data-action="([a-z-]+)"/g)].map(m => m[1]));

const used = new Set([...usedInJs, ...usedInHtml]);
const missing = [...used].filter(a => !actionHandlers.has(a));

check('每个 data-action 都有处理函数', missing.length === 0,
  missing.length === 0
    ? `共 ${used.size} 个动作（页面 ${usedInHtml.size} 个、脚本 ${usedInJs.size} 个），全部已接线`
    : `没有处理函数：${missing.join(', ')}`);

// 反过来也查一遍：定义了却没人用的动作多半是改名改了一半
const unused = [...actionHandlers].filter(a => !used.has(a));
check('没有定义了却没人用的动作', unused.length === 0,
  unused.length === 0 ? '无冗余分支' : `无人调用：${unused.join(', ')}`);

/* ---------- 5. 结论 ---------- */

console.log('');
if (failures === 0) {
  console.log('全部通过。');
  process.exit(0);
}

console.log(`存在 ${failures} 项失败。`);
process.exit(1);
