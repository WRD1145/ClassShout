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

/// 允许 HTML 的横幅。
///
/// 只给"要把拼出来的那几句话一条条摆出来"的地方用（见 submitTeacherCall），
/// 而且调用方必须**逐段 escapeHtml 过** —— 这里不做转义，是因为它要的就是
/// 分行的结构；把未转义的字符串传进来就是注入。
function setBannerHtml(id, html) {
  const el = document.getElementById(id);
  if (!html) { el.className = 'hidden'; el.innerHTML = ''; return; }
  el.className = 'banner ok';
  el.innerHTML = html;
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

    if (!result || !result.ok) {
      const message = (result && result.error) || '登录失败。';
      setBanner('loginError', message);

      // 控制台只接受管理员账号，而"这个账号还不存在"是登录失败最常见的原因：
      // 老师账号是在教师端 APP 上自己注册的，管理员账号则由服务器首次启动时生成。
      // 与其让人对着"账号或口令不正确"发呆，不如直接告诉他下一步该做什么。
      //
      // 说明一句取舍：服务端刻意不区分"账号不存在"与"口令错误"（那样可以被用来
      // 枚举账号）。这里只在控制台这一侧、对那句合并后的提示给出行动建议，
      // 并没有把服务端变成账号探测器。
      if (message.indexOf('账号或口令不正确') >= 0) {
        showOverlay('loginHelpOverlay');
      }

      return;
    }

    authToken = result.token;
    currentUser = result.user;
    sessionStorage.setItem('classshout_token', authToken);

    // 管理员进控制台，老师进"给自己班喊话"那一页 —— 两边共用同一个登录入口
    if (currentUser && currentUser.isAdmin) {
      await enterConsole();
    } else {
      await enterTeacherView();
    }
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
  document.getElementById('teacherView').classList.add('hidden');
  document.getElementById('loginView').classList.remove('hidden');
}

async function enterConsole() {
  document.getElementById('loginView').classList.add('hidden');
  document.getElementById('teacherView').classList.add('hidden');
  document.getElementById('consoleView').classList.remove('hidden');
  document.getElementById('whoami').textContent =
    (currentUser ? currentUser.displayName : '?') +
    (currentUser && currentUser.username ? '（' + currentUser.username + '）' : '');

  await loadOverview();
  await loadClassrooms();
  await loadUsers();
  await loadBindings();
}

/* ---------- 老师视图 ---------- */

// 老师勾了哪几个班（key 是 UUID）。刷新列表时保留选择，
// 否则每次刷新都要重新勾一遍。
let teacherSelection = {};

async function enterTeacherView() {
  document.getElementById('loginView').classList.add('hidden');
  document.getElementById('consoleView').classList.add('hidden');
  document.getElementById('teacherView').classList.remove('hidden');

  document.getElementById('teacherWhoami').textContent =
    (currentUser ? currentUser.displayName : '?') +
    (currentUser && currentUser.subject ? '（' + currentUser.subject + '）' : '') +
    ' · 老师';

  setBanner('teacherError', '');
  setBanner('teacherResult', '');
  setBanner('callError', '');
  setBanner('callResult', '');

  await loadTeacherClassrooms();
  await loadTeacherRoster();
}

/* ---------- 展示参数 ---------- */

// 与 App 里"这条怎么显示"那四个选项同一套取值（见 Core 的 ShoutDisplayOptions）。
// 空字符串表示"没指定"，由教室端用自己的默认值 —— 不填就不发送这个字段。
function readDisplayParams() {
  const display = document.getElementById('teacherDisplay').value;
  const fontSize = document.getElementById('teacherFontSize').value;
  const hold = document.getElementById('teacherHold').value;

  const params = {
    speak: document.getElementById('teacherSpeak').checked,
    interrupt: document.getElementById('teacherInterrupt').checked,
  };

  if (display) {
    params.display = display;
  }

  if (fontSize) {
    params.fontSize = fontSize;
  }

  const holdMs = parseInt(hold, 10);
  if (!isNaN(holdMs)) {
    params.holdMs = holdMs;
  }

  return params;
}

async function loadTeacherClassrooms() {
  const host = document.getElementById('teacherClassrooms');
  const list = await apiJson('/api/teacher/classrooms');

  if (!list || list.length === 0) {
    host.innerHTML = '<div class="empty">管理员还没有把班级授权给你。' +
      '让管理员在控制台的「教室」页点「授权给老师」，或者用分享链接把班级加到你的账号下。</div>';
    return;
  }

  host.innerHTML =
    '<table><thead><tr>' +
    '<th></th><th>班级</th><th>状态</th><th>最近活动</th>' +
    '</tr></thead><tbody>' +
    list.map(c => {
      const checked = teacherSelection[c.uuid] ? ' checked' : '';
      const status = c.online
        ? '<span class="chip ok">在线</span>'
        : '<span class="chip">离线</span>';

      return '<tr>' +
        '<td><input type="checkbox" data-teacher-classroom="' + escapeAttr(c.uuid) + '"' + checked + '></td>' +
        '<td>' + escapeHtml(c.name) + '</td>' +
        '<td>' + status + '</td>' +
        '<td>' + fmtTime(c.lastSeenAt) + '</td>' +
        '</tr>';
    }).join('') +
    '</tbody></table>';

  // 勾选状态记在 teacherSelection 里：列表一重建，勾过的还在
  host.querySelectorAll('input[data-teacher-classroom]').forEach(input => {
    input.addEventListener('change', () => {
      teacherSelection[input.dataset.teacherClassroom] = input.checked;
    });
  });
}

function setTeacherSelection(value) {
  document.querySelectorAll('input[data-teacher-classroom]').forEach(input => {
    input.checked = value;
    teacherSelection[input.dataset.teacherClassroom] = value;
  });
}

function selectedTeacherTargets() {
  return Object.keys(teacherSelection).filter(uuid => teacherSelection[uuid]);
}

async function submitTeacherShout() {
  setBanner('teacherError', '');
  setBanner('teacherResult', '');

  const text = (document.getElementById('teacherText').value || '').trim();
  const targets = selectedTeacherTargets();

  if (!text) {
    setBanner('teacherError', '写一句要朗读的内容。');
    return;
  }

  if (targets.length === 0) {
    setBanner('teacherError', '至少勾一个班级。');
    return;
  }

  // 展示参数与 App 里那一套一致：不选"按教室端默认"时才把字段发上去
  const display = readDisplayParams();

  const result = await apiJson('/api/teacher/shout', {
    method: 'POST',
    body: JSON.stringify({
      targetUuids: targets,
      text: text,
      speak: display.speak,
      interrupt: display.interrupt,
      display: display.display,
      fontSize: display.fontSize,
      holdMs: display.holdMs
    })
  });

  if (!result || !result.ok) {
    // 一间都没发出去时，把逐间的原因摆出来（"没授权""教室没了"是两回事）
    const detail = result && result.results
      ? result.results.filter(r => !r.ok).map(r => r.classroomName + '：' + r.error).join('；')
      : '';

    setBanner('teacherError', ((result && result.message) || '发送失败。') + (detail ? ' ' + detail : ''));
    return;
  }

  setBanner('teacherResult', result.message);
  await loadTeacherClassrooms();
}

/* ---------- 呼叫（名单与模板由教师端同步上来） ---------- */

// 选中的学生（key 是学生 Id）。与学生名单一起刷新时保留勾选。
let callSelection = {};
let callRoster = null;

async function loadTeacherRoster() {
  const body = document.getElementById('callBody');
  const empty = document.getElementById('callEmpty');
  const list = document.getElementById('callStudents');
  const templateSelect = document.getElementById('callTemplate');

  const snapshot = await apiJson('/api/teacher/roster');
  const rosters = snapshot && snapshot.rosters ? snapshot.rosters : [];

  // 当前这份名单：服务器上标记为"当前"的那份，没有就用第一份
  const active = rosters.find(r => r.id === snapshot.activeRosterId) || rosters[0];

  if (!active || !active.students || active.students.length === 0) {
    callRoster = null;
    body.classList.add('hidden');
    empty.classList.remove('hidden');
    return;
  }

  callRoster = active;
  body.classList.remove('hidden');
  empty.classList.add('hidden');

  const templates = snapshot.templates || [];
  const activeTemplate = templates.find(t => t.id === snapshot.activeTemplateId) || templates[0];

  templateSelect.innerHTML = templates.length === 0
    ? '<option value="">（服务器上没有模板）</option>'
    : templates.map(t => {
        const selected = activeTemplate && t.id === activeTemplate.id ? ' selected' : '';
        return '<option value="' + escapeAttr(t.id) + '"' + selected + '>' +
          escapeHtml(t.name) + '</option>';
      }).join('');

  list.innerHTML = active.students.map(s => {
    const checked = callSelection[s.id] ? ' checked' : '';
    const detail = [s.studentNo, s.shortName, s.group].filter(Boolean).join('，');

    return '<div class="pick-row">' +
      '<input type="checkbox" data-call-student="' + escapeAttr(s.id) + '"' + checked + '>' +
      '<span>' + escapeHtml(s.name) + '</span>' +
      (detail ? '<span class="pick-detail">（' + escapeHtml(detail) + '）</span>' : '') +
      '</div>';
  }).join('');

  list.querySelectorAll('input[data-call-student]').forEach(input => {
    input.addEventListener('change', () => {
      callSelection[input.dataset.callStudent] = input.checked;
    });
  });
}

function setCallSelection(value) {
  document.querySelectorAll('input[data-call-student]').forEach(input => {
    input.checked = value;
    callSelection[input.dataset.callStudent] = value;
  });
}

function selectedCallStudents() {
  return Object.keys(callSelection).filter(id => callSelection[id]);
}

/// 拼一次呼叫。preview 为真时只拼不发（教室里不会有任何动静）。
async function submitTeacherCall(preview) {
  setBanner('callError', '');
  setBanner('callResult', '');

  const students = selectedCallStudents();
  const targets = selectedTeacherTargets();
  const templateId = document.getElementById('callTemplate').value;

  if (students.length === 0) {
    setBanner('callError', '先勾几位学生。');
    return;
  }

  // 推送必须勾班级；预览可以先不勾 —— 服务器会用你的默认科目拼一份给你看
  if (targets.length === 0 && !preview) {
    setBanner('callError', '还没勾班级 —— 在上面「我的班级」里勾一个。');
    return;
  }

  if (!templateId) {
    setBanner('callError', '服务器上还没有呼叫模板：去 App 的「呼叫」页拼一个，再点「同步名单到服务器」。');
    return;
  }

  const display = readDisplayParams();

  const result = await apiJson('/api/teacher/call', {
    method: 'POST',
    body: JSON.stringify({
      targetUuids: targets,
      studentIds: students,
      templateId: templateId,
      previewOnly: !!preview,
      speak: display.speak,
      interrupt: display.interrupt,
      display: display.display,
      fontSize: display.fontSize,
      holdMs: display.holdMs
    })
  });

  if (!result || !result.ok) {
    const detail = result && result.results
      ? result.results.filter(r => !r.ok).map(r => r.classroomName + '：' + r.error).join('；')
      : '';

    setBanner('callError', ((result && result.message) || '呼叫失败。') + (detail ? ' ' + detail : ''));
    return;
  }

  // 拼出来的那几句话原样摆出来：老师要能看见"到底喊了什么"，
  // 而不是只看到一句"已发送"。每一段动态内容都先 escapeHtml。
  const lines = (result.messages || [])
    .map(m => '<div>' + escapeHtml(m) + '</div>')
    .join('');

  setBannerHtml('callResult',
    escapeHtml(preview ? '预览（还没有发出去）' : (result.message || '已发送')) +
    (lines ? '<div class="call-preview">' + lines + '</div>' : ''));
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

  // 用 textContent 而不是 innerHTML：这里没有任何需要标记的内容，
  // 不给未来的改动留下把服务器返回值当 HTML 解析的机会。
  const meta = document.getElementById('serverMeta');
  if (meta) {
    meta.textContent = '服务器版本 ' + (data.version || '未知') +
                       ' · 服务器时间 ' + fmtTime(data.serverTime);
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
    '<th></th><th>教室名</th><th>UUID</th><th>在线教师</th><th>注册时间</th><th>最后在线</th><th></th>' +
    '</tr></thead><tbody>' +
    list.map(c =>
      '<tr>' +
      '<td><input type="checkbox" class="share-pick" value="' + escapeAttr(c.uuid) + '"></td>' +
      '<td>' + escapeHtml(c.name) + '</td>' +
      '<td class="mono">' + escapeHtml(c.uuid) + '</td>' +
      '<td>' + (c.onlineTeachers > 0
        ? '<span class="chip ok">在线 ' + c.onlineTeachers + ' 人</span>'
        : '<span class="chip">无人在线</span>')
        + (c.boundTeachers > c.onlineTeachers
          ? ' <span class="muted-inline">绑定 ' + c.boundTeachers + '</span>'
          : '') + '</td>' +
      '<td>' + fmtTime(c.registeredAt) + '</td>' +
      '<td>' + fmtTime(c.lastSeenAt) + '</td>' +
      '<td class="cell-actions">' +
        '<button class="tonal small" data-action="shout"' +
          ' data-uuid="' + escapeAttr(c.uuid) + '"' +
          ' data-name="' + escapeAttr(c.name) + '">喊话</button>' +
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
let cachedBindings = [];

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
    '<th>姓名</th><th>任教科目</th><th>用户名</th><th>邮箱</th><th>状态</th><th>注册时间</th><th>最后登录</th><th></th>' +
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

      // 科目是教师端注册时填的，喊话来源会显示成"数学张老师"。
      // 控制台上单列一列，是因为同一所学校里重名的老师很常见，
      // 光看姓名分不清哪个账号该授权哪间教室。
      // 「改科目」这个动作也是为它准备的：注册时留空之后，别处就没地方补了；
      // 同一位老师在不同班教不同科目，也在那个对话框里按班级填。
      const subjectText = u.subject
        ? escapeHtml(u.subject) + (u.subjectByClassroom && Object.keys(u.subjectByClassroom).length > 0
            ? ' <span class="muted-inline">另 ' + Object.keys(u.subjectByClassroom).length + ' 个班不同</span>'
            : '')
        : (u.subjectByClassroom && Object.keys(u.subjectByClassroom).length > 0
            ? '<span class="muted-inline">按班级指定</span>'
            : '—');

      const subjectAction = u.isAdmin ? '' :
        '<button class="outlined small" data-action="subject"' +
          ' data-id="' + escapeAttr(u.id) + '"' +
          ' data-name="' + escapeAttr(u.displayName) + '">改科目</button>';

      return '<tr>' +
        '<td>' + escapeHtml(u.displayName) +
          (u.isAdmin ? ' <span class="chip info">内置管理员</span>' : '') + '</td>' +
        '<td>' + subjectText + '</td>' +
        '<td class="mono">' + (u.username ? escapeHtml(u.username) : '—') + '</td>' +
        '<td class="mono">' + (u.email ? escapeHtml(u.email) : '—') + '</td>' +
        '<td>' + status + '</td>' +
        '<td>' + (u.isAdmin ? '—' : fmtTime(u.createdAt)) + '</td>' +
        '<td>' + fmtTime(u.lastLoginAt) + '</td>' +
        '<td class="cell-actions">' + rowActions + subjectAction + '</td>' +
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
  cachedBindings = list || [];
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
      // 带上科目：同一所学校里重名的老师很常见，只列姓名容易授错人
      (u.subject ? '（' + escapeHtml(u.subject) + '）' : '') +
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

/* ---------- 弹层工具 ---------- */

function showOverlay(id) {
  const el = document.getElementById(id);
  if (el) el.classList.remove('hidden');
}

function hideOverlay(id) {
  const el = document.getElementById(id);
  if (el) el.classList.add('hidden');
}

function fieldValue(id) {
  const el = document.getElementById(id);
  return el ? el.value : '';
}

/* ---------- 添加账号 ---------- */

function openCreateUser() {
  ['newUsername', 'newEmail', 'newDisplayName', 'newSubject', 'newUserPassword'].forEach(id => {
    const el = document.getElementById(id);
    if (el) el.value = '';
  });

  setBanner('createUserError', '');
  showOverlay('createUserOverlay');

  const first = document.getElementById('newUsername');
  if (first) first.focus();
}

async function submitCreateUser() {
  const username = fieldValue('newUsername').trim();
  const email = fieldValue('newEmail').trim();
  const displayName = fieldValue('newDisplayName').trim();
  const subject = fieldValue('newSubject').trim();
  const password = fieldValue('newUserPassword');

  if (!username && !email) {
    setBanner('createUserError', '用户名与邮箱至少填一个。');
    return;
  }

  if (password.length < 6) {
    setBanner('createUserError', '初始口令至少 6 位。');
    return;
  }

  const result = await apiJson('/api/console/users', {
    method: 'POST',
    body: JSON.stringify({
      username: username || null,
      email: email || null,
      displayName: displayName || null,
      subject: subject || null,
      password: password
    })
  });

  if (result && result.ok) {
    hideOverlay('createUserOverlay');
    toast(result.message || '账号已创建。');
    await loadUsers();
  } else {
    setBanner('createUserError', (result && result.error) || '创建失败。');
  }
}

/* ---------- 批量导入 ---------- */

function openImportUsers() {
  setBanner('importError', '');
  showOverlay('importOverlay');

  const area = document.getElementById('importCsv');
  if (area) area.focus();
}

async function submitImportUsers() {
  const csv = fieldValue('importCsv');

  if (!csv.trim()) {
    setBanner('importError', '请把要导入的内容粘贴进来。');
    return;
  }

  const result = await apiJson('/api/console/users/import', {
    method: 'POST',
    body: JSON.stringify({ csv: csv })
  });

  if (!result) {
    setBanner('importError', '导入失败。');
    return;
  }

  await loadUsers();

  if (result.failed === 0) {
    hideOverlay('importOverlay');
    toast(`导入完成：成功 ${result.created} 条。`);
    return;
  }

  // 有失败行时不关对话框：把逐行说明留在眼前，管理员改完可以接着再导一次。
  // 失败原因里带着行号，比"3 条失败"有用得多。
  const failedLines = (result.details || []).filter(line => line.indexOf('已创建') < 0);
  setBanner('importError',
    `成功 ${result.created} 条，失败 ${result.failed} 条：\n` + failedLines.join('\n'), 'warn');
}

/* ---------- 喊话 ---------- */

let shoutTarget = null;

function openShout(uuid, name) {
  shoutTarget = { uuid: uuid, name: name };

  const label = document.getElementById('shoutClassroom');
  if (label) label.textContent = name;

  const input = document.getElementById('shoutText');
  if (input) input.value = '';

  setBanner('shoutError', '');
  showOverlay('shoutOverlay');
  if (input) input.focus();
}

async function submitShout() {
  const text = fieldValue('shoutText').trim();

  if (!text) {
    setBanner('shoutError', '请填写要朗读的内容。');
    return;
  }

  const result = await apiJson('/api/console/shout', {
    method: 'POST',
    body: JSON.stringify({ uuid: shoutTarget.uuid, text: text })
  });

  if (result && result.ok) {
    hideOverlay('shoutOverlay');
    toast(result.message || '已发送。');
  } else {
    setBanner('shoutError', (result && result.error) || '发送失败。');
  }
}

/* ---------- 分享班级连接 ---------- */

async function shareClassrooms() {
  const picked = Array.prototype.slice
    .call(document.querySelectorAll('.share-pick:checked'))
    .map(function (box) { return box.value; });

  // 一个都没勾就分享全部：开学时"把这些班都给这位老师"是最常见的用法，
  // 而现在正好有一屋子班要选，让他逐个勾是没必要的摩擦。
  const uuids = picked.length > 0
    ? picked
    : Array.prototype.slice.call(document.querySelectorAll('.share-pick')).map(function (box) { return box.value; });

  if (uuids.length === 0) {
    toast('还没有教室可以分享。');
    return;
  }

  const result = await apiJson('/api/console/share', {
    method: 'POST',
    body: JSON.stringify({ uuids: uuids })
  });

  if (!result || !result.ok) {
    toast((result && result.error) || '生成分享链接失败。');
    return;
  }

  // 链接是凭据，所以给一个能直接复制的输入框，而不是只在提示条上闪一下。
  const link = result.url;
  document.getElementById('shareCount').textContent = result.count;
  document.getElementById('shareLink').value = link;
  document.getElementById('shareExpires').textContent = fmtTime(result.expiresAt);
  setBanner('shareError', '');
  showOverlay('shareOverlay');
}

function copyShareLink() {
  const input = document.getElementById('shareLink');

  if (!input) return;

  input.select();

  try {
    navigator.clipboard.writeText(input.value);
    toast('链接已复制，发给老师即可。');
  } catch (err) {
    // 复制失败（旧浏览器、没有权限）不算错误：输入框已经全选，
    // 手动 Ctrl+C 一样能用。
    toast('请按 Ctrl+C 复制选中的链接。');
  }
}

/* ---------- 集体喊话 ---------- */

function openBroadcast() {
  const input = document.getElementById('broadcastText');
  if (input) input.value = '';

  setBanner('broadcastError', '');
  showOverlay('broadcastOverlay');
  if (input) input.focus();
}

async function submitBroadcast() {
  const text = fieldValue('broadcastText').trim();

  if (!text) {
    setBanner('broadcastError', '请填写要朗读的内容。');
    return;
  }

  const result = await apiJson('/api/console/broadcast', {
    method: 'POST',
    body: JSON.stringify({ text: text })
  });

  if (result && result.ok) {
    hideOverlay('broadcastOverlay');

    // 一间都没有时也要说清楚：否则管理员会以为"发出去了"，
    // 而教室里其实什么都没发生。
    toast(result.count > 0
      ? result.message
      : '当前没有在线教室，没有发送。');
  } else {
    setBanner('broadcastError', (result && result.error) || '发送失败。');
  }
}

/* ---------- 改任教科目对话框 ---------- */

let subjectTarget = null;

/**
 * 按班级的科目：这位老师被授权的班级各给一个输入框。
 *
 * 只列被授权的班级，而不是全校所有教室：一位老师实际会去喊的就是这几个班，
 * 把五十间教室全铺出来，反而要找半天。
 */
function subjectClassroomRows(userId) {
  return (cachedBindings || [])
    .filter(b => b.userId === userId)
    .map(b => ({ uuid: b.uuid, name: b.classroomName }));
}

function openSubject(userId, name) {
  subjectTarget = userId;

  const user = (cachedUsers || []).find(u => u.id === userId);
  const byClassroom = (user && user.subjectByClassroom) || {};

  document.getElementById('subjectName').textContent = name;
  document.getElementById('subjectInput').value = (user && user.subject) || '';

  const rows = subjectClassroomRows(userId);
  const host = document.getElementById('subjectClassrooms');

  if (rows.length === 0) {
    host.innerHTML = '<p class="empty">这位老师还没有被授权的班级 —— 按班级的科目' +
      '要等「教室」页把班级授权给他之后才有意义。没有单独指定的班级都用上面的默认科目。</p>';
  } else {
    host.innerHTML = rows.map(row => {
      // 服务器存储时键的大小写按写入时的原样，比较要忽略大小写
      const key = Object.keys(byClassroom)
        .find(k => k.toLowerCase() === row.uuid.toLowerCase());
      const value = key ? byClassroom[key] : '';

      return '<label for="subj-' + escapeAttr(row.uuid) + '">' + escapeHtml(row.name) + '</label>' +
        '<input id="subj-' + escapeAttr(row.uuid) + '" type="text" autocomplete="off"' +
        ' data-uuid="' + escapeAttr(row.uuid) + '"' +
        ' placeholder="留空＝用上面的默认科目"' +
        ' value="' + escapeAttr(value || '') + '">';
    }).join('');
  }

  setBanner('subjectError', '');
  document.getElementById('subjectOverlay').classList.remove('hidden');
  document.getElementById('subjectInput').focus();
}

function closeSubject() {
  subjectTarget = null;
  document.getElementById('subjectOverlay').classList.add('hidden');
}

async function submitSubject() {
  const value = document.getElementById('subjectInput').value.trim();

  const byClassroom = {};
  document.querySelectorAll('#subjectClassrooms input[data-uuid]').forEach(el => {
    const subject = el.value.trim();
    if (subject) {
      byClassroom[el.dataset.uuid] = subject;
    }
  });

  const result = await apiJson('/api/console/users/' + encodeURIComponent(subjectTarget) + '/subject', {
    method: 'POST',
    body: JSON.stringify({ value: value || null, byClassroom: byClassroom })
  });

  if (result && result.ok) {
    hideOverlay('subjectOverlay');
    subjectTarget = null;
    const perClass = Object.keys(byClassroom).length;
    toast(perClass > 0
      ? `已保存：默认科目「${value || '（空）'}」，另有 ${perClass} 个班单独指定。`
      : (value ? `已把任教科目改成「${value}」。` : '已清空任教科目。'));
    await loadUsers();
  } else {
    setBanner('subjectError', (result && result.error) || '修改失败。');
  }
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

  subject: (el) => openSubject(el.dataset.id, el.dataset.name),
  'close-subject': () => closeSubject(),
  'submit-subject': () => submitSubject(),

  revoke: (el) => revokeBinding(
    el.dataset.userId, el.dataset.uuid, el.dataset.userName, el.dataset.classroomName),

  'open-create-user': () => openCreateUser(),
  'close-create-user': () => hideOverlay('createUserOverlay'),
  'submit-create-user': () => submitCreateUser(),

  'open-import-users': () => openImportUsers(),
  'close-import': () => hideOverlay('importOverlay'),
  'submit-import': () => submitImportUsers(),

  shout: (el) => openShout(el.dataset.uuid, el.dataset.name),
  'close-shout': () => hideOverlay('shoutOverlay'),
  'submit-shout': () => submitShout(),

  'share-classrooms': () => shareClassrooms(),

  'open-broadcast': () => openBroadcast(),
  'close-broadcast': () => hideOverlay('broadcastOverlay'),
  'close-share': () => hideOverlay('shareOverlay'),
  'copy-share': () => copyShareLink(),
  'submit-broadcast': () => submitBroadcast(),

  'close-login-help': () => hideOverlay('loginHelpOverlay'),

  // 老师视图
  'teacher-shout': () => submitTeacherShout(),
  'teacher-clear': () => { document.getElementById('teacherText').value = ''; },
  'teacher-select-all': () => setTeacherSelection(true),
  'teacher-select-none': () => setTeacherSelection(false),
  'teacher-refresh': () => loadTeacherClassrooms(),

  // 呼叫（名单与模板由教师端同步到服务器）
  'call-preview': () => submitTeacherCall(true),
  'call-send': () => submitTeacherCall(false),
  'call-select-all': () => setCallSelection(true),
  'call-select-none': () => setCallSelection(false),
  'call-refresh': () => loadTeacherRoster(),

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
    } else if (currentUser) {
      // 老师账号登进来是"给自己的班喊话"，不是"被拦在门外"
      await enterTeacherView();
    } else {
      showLogin();
    }
  } catch (e) {
    showLogin();
  }
})();
