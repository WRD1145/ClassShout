/* ==========================================================================
   控制台前端。所有数据都来自服务器自身的接口，页面本身不含任何业务逻辑。
   ========================================================================== */

let authToken = null;
let currentUser = null;

/* ---------- 基础请求 ---------- */

async function api(path, options = {}) {
  const headers = Object.assign({}, options.headers || {});
  if (authToken) headers['X-Auth-Token'] = authToken;
  if (options.body && !headers['Content-Type']) headers['Content-Type'] = 'application/json';

  const response = await fetch(path, Object.assign({}, options, { headers }));
  if (response.status === 401) {
    // 令牌失效就退回登录页，而不是让每个操作各自报错
    showLogin();
    throw new Error('会话已失效，请重新登录。');
  }
  return response;
}

async function apiJson(path, options) {
  const response = await api(path, options);
  const text = await response.text();
  return text ? JSON.parse(text) : null;
}

/* ---------- 提示 ---------- */

function toast(message) {
  const el = document.createElement('div');
  el.className = 'toast';
  el.textContent = message;
  document.body.appendChild(el);
  setTimeout(() => el.remove(), 2600);
}

function setBanner(id, message, kind) {
  const el = document.getElementById(id);
  if (!message) { el.className = 'hidden'; el.innerHTML = ''; return; }
  el.className = 'banner ' + (kind || 'error');
  el.textContent = message;
}

/* ---------- 登录 ---------- */

async function doLogin() {
  const account = document.getElementById('account').value.trim();
  const password = document.getElementById('password').value;
  setBanner('loginError', '');

  if (!account || !password) {
    setBanner('loginError', '请填写账号与口令。');
    return;
  }

  try {
    const result = await apiJson('/api/auth/login', {
      method: 'POST',
      body: JSON.stringify({ account, password })
    });

    if (!result.ok) {
      setBanner('loginError', result.error || '登录失败。');
      return;
    }

    authToken = result.token;
    currentUser = result.user;
    sessionStorage.setItem('classshout_token', authToken);
    await enterConsole();
  } catch (e) {
    setBanner('loginError', '无法连接服务器：' + e.message);
  }
}

async function doLogout() {
  try { await api('/api/auth/logout', { method: 'POST' }); } catch (e) { /* 忽略 */ }
  authToken = null;
  currentUser = null;
  sessionStorage.removeItem('classshout_token');
  showLogin();
}

function showLogin() {
  authToken = null;
  document.getElementById('consoleView').classList.add('hidden');
  document.getElementById('loginView').classList.remove('hidden');
}

async function enterConsole() {
  document.getElementById('loginView').classList.add('hidden');
  document.getElementById('consoleView').classList.remove('hidden');
  document.getElementById('whoami').textContent =
    (currentUser ? currentUser.displayName : '?') +
    (currentUser && currentUser.username ? '（' + currentUser.username + '）' : '');

  await loadOverview();
  await loadClassrooms();
  await loadUsers();
  await loadBindings();
}

/* ---------- 标签页 ---------- */

function showTab(name) {
  document.querySelectorAll('.tab').forEach(t => t.classList.toggle('active', t.dataset.tab === name));
  ['overview', 'classrooms', 'users', 'bindings', 'help'].forEach(t => {
    document.getElementById('tab-' + t).classList.toggle('hidden', t !== name);
  });
}

/* ---------- 概览 ---------- */

async function loadOverview() {
  const data = await apiJson('/api/console/overview');
  const stats = [
    { num: data.classrooms, label: '已注册教室' },
    { num: data.users, label: '用户账号' },
    { num: data.onlineTeachers, label: '当前在线教师端' },
    { num: data.bindings, label: '班级授权' }
  ];
  document.getElementById('stats').innerHTML = stats.map(s =>
    '<div class="stat"><div class="num">' + s.num + '</div><div class="label">' + s.label + '</div></div>'
  ).join('');

  if (data.adminPasswordIsInitial) {
    setBanner('consoleBanner',
      '管理员仍在使用首次启动时自动生成的口令，建议在「用户」页里尽快修改。', 'warn');
  } else {
    setBanner('consoleBanner', '');
  }
}

/* ---------- 教室 ---------- */

function fmtTime(value) {
  if (!value) return '—';
  const d = new Date(value);
  const pad = n => String(n).padStart(2, '0');
  return d.getFullYear() + '-' + pad(d.getMonth() + 1) + '-' + pad(d.getDate()) +
         ' ' + pad(d.getHours()) + ':' + pad(d.getMinutes());
}

async function loadClassrooms() {
  const list = await apiJson('/api/console/classrooms');
  const host = document.getElementById('classroomList');

  if (!list || list.length === 0) {
    host.innerHTML = '<div class="empty">还没有教室连接过本服务器。</div>';
    return;
  }

  host.innerHTML =
    '<table><thead><tr>' +
    '<th>教室名</th><th>UUID</th><th>在线教师</th><th>注册时间</th><th>最后在线</th><th></th>' +
    '</tr></thead><tbody>' +
    list.map(c =>
      '<tr>' +
      '<td>' + escapeHtml(c.name) + '</td>' +
      '<td class="mono">' + escapeHtml(c.uuid) + '</td>' +
      '<td>' + (c.onlineTeachers > 0
        ? '<span class="chip ok">' + c.onlineTeachers + ' 个</span>'
        : '<span class="chip">无</span>') + '</td>' +
      '<td>' + fmtTime(c.registeredAt) + '</td>' +
      '<td>' + fmtTime(c.lastSeenAt) + '</td>' +
      '<td class="cell-actions">' +
        '<button class="tonal small" data-action="grant"' +
          ' data-uuid="' + escapeAttr(c.uuid) + '"' +
          ' data-name="' + escapeAttr(c.name) + '">授权给老师</button>' +
        '<button class="danger small" data-action="delete-classroom"' +
          ' data-uuid="' + escapeAttr(c.uuid) + '"' +
          ' data-name="' + escapeAttr(c.name) + '">删除</button></td>' +
      '</tr>'
    ).join('') +
    '</tbody></table>';
}

async function deleteClassroom(uuid, name) {
  if (!confirm('确定删除教室「' + name + '」的注册记录吗？\n\n删除后该教室需要在教室端重新连接服务器完成注册，' +
               '已绑定的教师端也会立即失效。\n\n老师忘记口令时正是用这个办法重置。')) return;

  const result = await apiJson('/api/console/classrooms/' + encodeURIComponent(uuid), { method: 'DELETE' });
  toast(result && result.ok ? '已删除。' : '删除失败。');
  await Promise.all([loadClassrooms(), loadOverview()]);
}

/* ---------- 用户 ---------- */

let cachedUsers = [];

async function loadUsers() {
  const list = await apiJson('/api/console/users');
  cachedUsers = list || [];
  const host = document.getElementById('userList');

  if (!list || list.length === 0) {
    host.innerHTML = '<div class="empty">还没有老师注册账号。</div>';
    return;
  }

  host.innerHTML =
    '<table><thead><tr>' +
    '<th>姓名</th><th>用户名</th><th>邮箱</th><th>状态</th><th>注册时间</th><th>最后登录</th><th></th>' +
    '</tr></thead><tbody>' +
    list.map(u => {
      const status = u.disabled
        ? '<span class="chip bad">已停用</span>'
        : '<span class="chip ok">正常</span>';

      // 内置管理员没有"停用"这个动作：它不是用户库里的一条记录，
      // 停掉等于把自己锁在控制台外面，所以那个按钮对它干脆不出现。
      const rowActions =
        '<button class="tonal small" data-action="reset"' +
          ' data-id="' + escapeAttr(u.id) + '"' +
          ' data-name="' + escapeAttr(u.displayName) + '">重置口令</button>' +
        (u.isAdmin ? '' :
          '<button class="outlined small" data-action="toggle-disabled"' +
            ' data-id="' + escapeAttr(u.id) + '"' +
            ' data-disabled="' + (!u.disabled) + '">' +
            (u.disabled ? '启用' : '停用') + '</button>');

      return '<tr>' +
        '<td>' + escapeHtml(u.displayName) +
          (u.isAdmin ? ' <span class="chip info">内置管理员</span>' : '') + '</td>' +
        '<td class="mono">' + (u.username ? escapeHtml(u.username) : '—') + '</td>' +
        '<td class="mono">' + (u.email ? escapeHtml(u.email) : '—') + '</td>' +
        '<td>' + status + '</td>' +
        '<td>' + (u.isAdmin ? '—' : fmtTime(u.createdAt)) + '</td>' +
        '<td>' + fmtTime(u.lastLoginAt) + '</td>' +
        '<td class="cell-actions">' + rowActions + '</td>' +
      '</tr>';
    }).join('') +
    '</tbody></table>';
}

async function toggleDisabled(id, disabled) {
  const result = await apiJson('/api/console/users/' + encodeURIComponent(id) + '/disabled', {
    method: 'POST',
    body: JSON.stringify({ value: disabled })
  });
  toast(result && result.ok ? '已更新。' : '操作失败。');
  await loadUsers();
  await loadBindings();
}

/* ---------- 班级授权 ---------- */

async function loadBindings() {
  const list = await apiJson('/api/console/bindings');
  const host = document.getElementById('bindingList');

  if (!list || list.length === 0) {
    host.innerHTML = '<div class="empty">还没有授权记录。到「教室」页点「授权给老师」即可指派。' +
      '（内置管理员默认对所有班级可用，不需要授权。）</div>';
    return;
  }

  host.innerHTML =
    '<table><thead><tr>' +
    '<th>老师</th><th>班级</th><th>授权人</th><th>授权时间</th><th></th>' +
    '</tr></thead><tbody>' +
    list.map(b =>
      '<tr>' +
      '<td>' + escapeHtml(b.userDisplayName) + '</td>' +
      '<td>' + escapeHtml(b.classroomName) + '</td>' +
      '<td>' + escapeHtml(b.grantedBy) + '</td>' +
      '<td>' + fmtTime(b.grantedAt) + '</td>' +
      '<td class="cell-actions-plain"><button class="outlined small" data-action="revoke"' +
        ' data-user-id="' + escapeAttr(b.userId) + '"' +
        ' data-uuid="' + escapeAttr(b.uuid) + '"' +
        ' data-user-name="' + escapeAttr(b.userDisplayName) + '"' +
        ' data-classroom-name="' + escapeAttr(b.classroomName) + '">取消授权</button></td>' +
      '</tr>'
    ).join('') +
    '</tbody></table>';
}

let grantTarget = null;

function openGrant(uuid, name) {
  grantTarget = { uuid: uuid, name: name };
  document.getElementById('grantName').textContent = name;
  setBanner('grantError', '');

  const select = document.getElementById('grantUser');

  // 管理员默认就对所有班级可用，把它列进"授权给谁"只会误导人
  const candidates = (cachedUsers || []).filter(u => !u.isAdmin);

  if (candidates.length === 0) {
    select.innerHTML = '<option value="">（还没有老师注册账号）</option>';
  } else {
    select.innerHTML = candidates.map(u =>
      '<option value="' + u.id + '">' + escapeHtml(u.displayName) +
      (u.disabled ? '（已停用）' : '') + '</option>'
    ).join('');
  }

  document.getElementById('grantOverlay').classList.remove('hidden');
}

function closeGrant() {
  grantTarget = null;
  document.getElementById('grantOverlay').classList.add('hidden');
}

async function submitGrant() {
  const userId = document.getElementById('grantUser').value;
  if (!userId) {
    setBanner('grantError', '请先选择一位老师。');
    return;
  }

  const result = await apiJson('/api/console/bindings', {
    method: 'POST',
    body: JSON.stringify({ userId: userId, uuid: grantTarget.uuid })
  });

  if (result && result.ok) {
    closeGrant();
    toast(result.message || '已授权。');
    await Promise.all([loadBindings(), loadClassrooms()]);
  } else {
    setBanner('grantError', (result && result.error) || '授权失败。');
  }
}

async function revokeBinding(userId, uuid, userName, classroomName) {
  if (!confirm('取消「' + userName + '」对「' + classroomName + '」的授权吗？\n\n' +
               '该老师将无法再一键绑定这个班级，已经建立的连接也会立即断开。')) return;

  const result = await apiJson('/api/console/bindings?userId=' + encodeURIComponent(userId) +
                               '&uuid=' + encodeURIComponent(uuid), { method: 'DELETE' });
  toast(result && result.ok ? '已取消授权。' : '取消失败。');
  await Promise.all([loadBindings(), loadClassrooms()]);
}

/* ---------- 重置口令对话框 ---------- */

let resetTarget = null;

function openReset(userId, name) {
  resetTarget = userId;
  document.getElementById('resetName').textContent = name;
  document.getElementById('resetInput').value = '';
  setBanner('resetError', '');
  document.getElementById('resetOverlay').classList.remove('hidden');
  document.getElementById('resetInput').focus();
}

function closeReset() {
  resetTarget = null;
  document.getElementById('resetOverlay').classList.add('hidden');
}

async function submitReset() {
  const password = document.getElementById('resetInput').value;
  if (password.length < 6) {
    setBanner('resetError', '口令至少 6 位。');
    return;
  }

  const result = await apiJson('/api/console/password', {
    method: 'POST',
    body: JSON.stringify({ userId: resetTarget, newPassword: password })
  });

  if (result && result.ok) {
    closeReset();
    toast(result.message || '口令已重置。');
    await loadUsers();
  } else {
    setBanner('resetError', (result && result.error) || '重置失败。');
  }
}

/* ---------- 转义 ----------
 *
 * 这里只负责一件事：把任意文本安全地放进 HTML 文本节点或属性值。
 *
 * 旧实现是 escapeHtml 之后再 .replace(/'/g, "\\'")，把内容塞进内联事件属性
 * （onclick 那类）里。那个做法有个致命缺口：反斜杠没被处理。
 * 教室名与老师显示名都能由匿名接口写入，构造 \');alert(1)// 会让转义产出
 * \\');alert(1)//，而在 JS 里 '\\' 是合法的"单个反斜杠"字符串，
 * 后面的 );alert(1);// 就自由了 —— 存储型 XSS，可直接读走管理员的令牌。
 *
 * 根因不是"转义函数写得不够好"，而是内联事件里本来就不该拼数据：
 * 那要求同时满足 HTML 属性和 JS 字符串两层转义，漏掉任何一层都完蛋。
 * 现在一律走 data-* 属性加事件委托（见下面的 click 分发），
 * 只剩 HTML 属性转义这一层，也就不可能漏。
 */

function escapeHtml(text) {
  return String(text == null ? '' : text)
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;')
    .replace(/"/g, '&quot;')
    .replace(/'/g, '&#39;');
}

// 属性值统一用双引号包裹，所以 & 和 " 是必须的；
// 单引号与尖括号一并转掉，免得以后有人改成单引号包裹时又留下缺口。
const escapeAttr = escapeHtml;

/* ---------- 事件委托 ----------
 *
 * 页面里不再有任何内联 on* 属性。这么做有两个好处：
 *   1. 上面那条注入路径从根上消失 —— 数据只进属性，不进代码；
 *   2. CSP 可以收紧到 script-src 'self'。没有 nonce 时，内联脚本
 *      和内联事件处理器都会被浏览器拒绝，所以这一步也是 #33 的前提。
 *
 * 按钮只声明"做什么"以及需要的数据，由下面这一处分发。
 */

const actions = {
  login: () => doLogin(),
  logout: () => doLogout(),
  tab: (el) => showTab(el.dataset.tab),

  grant: (el) => openGrant(el.dataset.uuid, el.dataset.name),
  'delete-classroom': (el) => deleteClassroom(el.dataset.uuid, el.dataset.name),

  reset: (el) => openReset(el.dataset.id, el.dataset.name),
  'toggle-disabled': (el) => toggleDisabled(el.dataset.id, el.dataset.disabled === 'true'),

  revoke: (el) => revokeBinding(
    el.dataset.userId, el.dataset.uuid, el.dataset.userName, el.dataset.classroomName),

  'close-grant': () => closeGrant(),
  'submit-grant': () => submitGrant(),
  'close-reset': () => closeReset(),
  'submit-reset': () => submitReset(),
};

document.addEventListener('click', (event) => {
  const el = event.target.closest('[data-action]');
  if (!el) return;

  const handler = actions[el.dataset.action];
  if (!handler) return;

  event.preventDefault();
  handler(el);
});

/* ---------- 启动 ---------- */

(async function boot() {
  document.getElementById('password').addEventListener('keydown', e => {
    if (e.key === 'Enter') doLogin();
  });

  // 刷新页面后尽量保持登录状态；令牌无效会被 api() 统一打回登录页
  const saved = sessionStorage.getItem('classshout_token');
  if (!saved) return;

  authToken = saved;
  try {
    currentUser = await apiJson('/api/auth/me');
    if (currentUser && currentUser.isAdmin) {
      await enterConsole();
    } else {
      // 教师账号不应进入管理控制台
      setBanner('loginError', '该账号不是管理员，无法进入控制台。');
      showLogin();
    }
  } catch (e) {
    showLogin();
  }
})();
