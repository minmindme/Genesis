/* ============================================================
   GENESIS / showcase — vanilla JS, no build, no dependencies.
   The demo is a front-end simulation of a Role=8 schema.
   Real runtime: POST /api/genesis { action: "GetSystemSchema" }.
   ============================================================ */
(() => {
  'use strict';

  const $ = (sel, root = document) => root.querySelector(sel);
  const $$ = (sel, root = document) => Array.from(root.querySelectorAll(sel));
  const reduced = window.matchMedia('(prefers-reduced-motion: reduce)').matches;
  const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, reduced ? 0 : ms));

  const META_TYPE = '00000000-0000-0000-0000-000000000001';
  const META_ENGINE = '00000000-0000-0000-0000-000000000002';

  const escapeCode = (value) => String(value)
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;');

  const escapeHtml = (value) => escapeCode(value).replace(/"/g, '&quot;');

  const formatNum = (value) => new Intl.NumberFormat('en-US').format(value);

  /* deterministic fake GUIDs so repeated renders look stable */
  const fakeGuid = (seed) => {
    let h1 = 0x811c9dc5;
    let h2 = 0x9e3779b9;
    let h3 = 0x85ebca6b;
    for (let i = 0; i < seed.length; i += 1) {
      const c = seed.charCodeAt(i);
      h1 = Math.imul(h1 ^ c, 16777619);
      h2 = Math.imul(h2 + c, 2654435761);
      h3 = Math.imul(h3 ^ c, 2246822519);
    }
    const hex = (n) => (n >>> 0).toString(16).padStart(8, '0');
    const a = hex(h1);
    const b = hex(h2);
    const c = hex(h3);
    return `${a}-${b.slice(0, 4)}-4${b.slice(4, 7)}-a${c.slice(0, 3)}-${c.slice(3, 7)}${a.slice(0, 8)}`;
  };

  /* ---------------- data ---------------- */

  const SYSTEMS = {
    book: {
      id: 'book',
      name: '图书管理',
      type: 'Book',
      mode: 'Graph',
      ui: 'BookForm',
      prompt: '建个图书管理系统，书有书名、作者和出版年份。',
      props: [
        { name: 'Title', type: 'String' },
        { name: 'Author', type: 'String' },
        { name: 'Year', type: 'Int' }
      ],
      fields: [
        { name: 'Title', label: '书名', control: 'Input', type: 'text', placeholder: '例如：百年孤独' },
        { name: 'Author', label: '作者', control: 'Input', type: 'text', placeholder: '例如：加西亚·马尔克斯' },
        { name: 'Year', label: '出版年份', control: 'Number', type: 'number', placeholder: '1967' }
      ],
      seed: [
        { Title: '百年孤独', Author: '加西亚·马尔克斯', Year: 1967 },
        { Title: '1984', Author: '乔治·奥威尔', Year: 1949 },
        { Title: '小王子', Author: '圣埃克苏佩里', Year: 1943 }
      ],
      records: []
    },
    ticket: {
      id: 'ticket',
      name: '工单系统',
      type: 'Ticket',
      mode: 'Graph',
      ui: 'TicketForm',
      prompt: '建个工单系统，要有标题、状态、审批人和优先级。',
      props: [
        { name: 'Title', type: 'String' },
        { name: 'Status', type: 'String' },
        { name: 'Assignee', type: 'String' },
        { name: 'Priority', type: 'String' }
      ],
      fields: [
        { name: 'Title', label: '标题', control: 'Input', type: 'text', placeholder: '例如：打印机无法连接' },
        { name: 'Status', label: '状态', control: 'Select', options: ['待处理', '处理中', '已完成'] },
        { name: 'Assignee', label: '审批人', control: 'Input', type: 'text', placeholder: '例如：张三' },
        { name: 'Priority', label: '优先级', control: 'Select', options: ['P0', 'P1', 'P2'] }
      ],
      seed: [
        { Title: '打印机无法连接', Status: '待处理', Assignee: '张三', Priority: 'P1' },
        { Title: '门禁权限申请', Status: '处理中', Assignee: '李四', Priority: 'P0' },
        { Title: '会议室预订异常', Status: '已完成', Assignee: '王五', Priority: 'P2' }
      ],
      records: []
    },
    student: {
      id: 'student',
      name: '学生管理',
      type: 'Student',
      mode: 'Graph',
      ui: 'StudentForm',
      prompt: '建个学生管理系统，学生有姓名、年龄和班级。',
      props: [
        { name: 'Name', type: 'String' },
        { name: 'Age', type: 'Int' },
        { name: 'Class', type: 'String' }
      ],
      fields: [
        { name: 'Name', label: '姓名', control: 'Input', type: 'text', placeholder: '例如：张小明' },
        { name: 'Age', label: '年龄', control: 'Number', type: 'number', placeholder: '12' },
        { name: 'Class', label: '班级', control: 'Input', type: 'text', placeholder: '例如：初一（3）班' }
      ],
      seed: [
        { Name: '张小明', Age: 12, Class: '初一（3）班' },
        { Name: '李华', Age: 13, Class: '初二（1）班' },
        { Name: '王芳', Age: 12, Class: '初一（3）班' }
      ],
      records: []
    },
    device: {
      id: 'device',
      name: '设备台账',
      type: 'Device',
      mode: 'Snapshot',
      ui: 'DeviceForm',
      prompt: '建设备台账，记录设备名、位置和状态，整对象存储。',
      props: [
        { name: 'Name', type: 'String' },
        { name: 'Location', type: 'String' },
        { name: 'Status', type: 'String' }
      ],
      fields: [
        { name: 'Name', label: '设备名', control: 'Input', type: 'text', placeholder: '例如：MacBook Pro' },
        { name: 'Location', label: '位置', control: 'Input', type: 'text', placeholder: '例如：3F 研发区' },
        { name: 'Status', label: '状态', control: 'Select', options: ['在用', '维修', '闲置'] }
      ],
      seed: [
        { Name: 'MacBook Pro', Location: '3F 研发区', Status: '在用' },
        { Name: '投影仪', Location: '2F 会议室', Status: '维修' },
        { Name: '打印机', Location: '1F 前台', Status: '闲置' }
      ],
      records: []
    }
  };

  const ROLES = [
    {
      id: 1, label: 'Meta', name: '元数据',
      desc: '类型与属性的定义行。引擎从这里读出「我是谁」：有哪些字段、字段是什么类型。',
      sample: { Id: '...0001', TypeId: META_TYPE, Role: 1, ParentId: null, Value: 'Book', Version: 1 }
    },
    {
      id: 2, label: 'Data', name: '业务数据',
      desc: '实体行、属性行、边行都可能是 Role=2。区别不在角色，而在 TypeId 指向哪一种元数据。',
      sample: { Id: '8f3a...', TypeId: '...a1c2', Role: 2, ParentId: '...b7d1', Value: '百年孤独', Version: 1 }
    },
    {
      id: 3, label: 'Edge', name: '关系边', deprecated: true,
      desc: '第 13 轮后废弃。边不再是特殊身份：它是 Role=2 的普通行，用 EdgeType 的 TypeId 区分。',
      sample: { Id: '...e3', TypeId: '...edge', Role: 3, ParentId: '...from', Value: 'SNAP1:{"To":"...to"}', Version: 1 }
    },
    {
      id: 4, label: 'Engine', name: '引擎指令',
      desc: '路由、存储模式、类绑定。改这一行 = 改引擎行为；引擎从池中自举这些规则。',
      sample: { Id: '...0902', TypeId: META_ENGINE, Role: 4, ParentId: null, Value: 'StorageMode=Snapshot|TargetTypeId=...', Version: 1 }
    },
    {
      id: 5, label: 'Policy', name: '安全策略',
      desc: '加密、租户隔离、限流。执行器是代码，选择与参数是数据；换算法只改一行策略。',
      sample: { Id: '...0904', TypeId: META_ENGINE, Role: 5, ParentId: null, Value: 'Policy=EncryptValue|Algo=AesGcm|Enabled=true', Version: 1 }
    },
    {
      id: 6, label: 'History', name: '历史行',
      desc: 'upsert 覆盖前的旧行。ParentId 指向当前行，Version 归属被覆盖前的实体版本——「过去」也是行。',
      sample: { Id: '...h1', TypeId: '...a1c2', Role: 6, ParentId: '...b7d1', Value: '张三', Version: 2 }
    },
    {
      id: 7, label: 'Command', name: 'CLI 命令',
      desc: '工作流定义：命令名、参数、步骤编排。命令树即数据，CLI 只是一个哑巴壳子。',
      sample: { Id: '...0920', TypeId: META_ENGINE, Role: 7, ParentId: null, Value: 'Command=CreateEntity|Args=TypeName,Props|Steps=InsertTypeMeta;ReloadCache', Version: 1 }
    },
    {
      id: 8, label: 'UI', name: '界面描述',
      desc: '通用渲染器的数据源。读一行 Role=8，长出一个表单——前端不需要任何业务组件。',
      sample: { Id: '...0930', TypeId: META_ENGINE, Role: 8, ParentId: null, Value: 'UI=BookForm|Type=Form|Entity=Book|Fields=Title:Input,Author:Input|Action=InsertData', Version: 1 }
    }
  ];

  const ENGINES = [
    { name: '逐行 + 磁盘表', short: 'row-by-row · SQL Server', write: 15419, read: 4810, total: 20311, throughput: 492, ipc: '60,001', acid: '完整' },
    { name: 'BulkCopy + 磁盘表', short: 'bulk · SQL Server', write: 1858, read: 655, total: 2528, throughput: 3955, ipc: '2', acid: '完整' },
    { name: 'TVP + 内存优化表', short: 'in-memory OLTP · SQL Server', write: 809, read: 611, total: 1458, throughput: 6858, ipc: '2', acid: '完整' },
    { name: 'ConcurrentDictionary', short: 'in-process · zero IPC', write: 36, read: 23, total: 137, throughput: 72992, ipc: '0', acid: '重启即丢' },
    { name: 'FASTER KV', short: 'embedded LSM · persistent', write: 81, read: 38, total: 176, throughput: 56818, ipc: '0', acid: '最终一致' }
  ];

  const THROUGHPUT = [
    { label: '逐行 SQL', value: 492, display: '492' },
    { label: 'BulkCopy', value: 3955, display: '3,955' },
    { label: 'TVP 内存表', value: 6858, display: '6,858' },
    { label: '进程内字典', value: 72992, display: '72,992', best: true },
    { label: 'FASTER KV', value: 56818, display: '56,818' }
  ];

  const REVERSAL = [
    { label: 'SqlServer B+ 树', value: 2, display: '2 ms', best: true },
    { label: 'InMemory 全扫', value: 58, display: '58 ms' },
    { label: 'FASTER 全扫', value: 154, display: '154 ms' }
  ];

  /* ---------------- syntax highlight ---------------- */

  const tokenRe = /("(?:\\u[a-fA-F0-9]{4}|\\[^u]|[^\\"])*")(\s*:)?|\b(true|false|null)\b|-?\d+(?:\.\d+)?(?:[eE][+\-]?\d+)?/g;

  const highlightJson = (text) => escapeCode(text).replace(tokenRe, (match, str, colon, bool) => {
    if (str) {
      if (colon) return `<span class="tok-key">${str}</span><span class="tok-punc">${colon}</span>`;
      return `<span class="tok-str">${str}</span>`;
    }
    if (bool) return `<span class="tok-bool">${bool}</span>`;
    return `<span class="tok-num">${match}</span>`;
  });

  const miniJson = (value) => escapeCode(JSON.stringify(value, null, 2)).replace(tokenRe, (match, str, colon, bool) => {
    if (str) {
      if (colon) return `<span class="k">${str}</span><span class="p">${colon}</span>`;
      return `<span class="s">${str}</span>`;
    }
    if (bool) return `<span class="z">${bool}</span>`;
    return `<span class="n">${match}</span>`;
  });

  /* ---------------- demo model ---------------- */

  const recordsFor = (system) => system.records;

  const systemJson = (system) => ({
    Entities: [{
      Name: system.type,
      Mode: system.mode,
      Props: system.props.map((p) => `${p.name}:${p.type}`).join(',')
    }],
    Uis: [{
      Name: system.ui,
      Entity: system.type,
      Fields: system.fields.map((f) => `${f.name}:${f.control}`).join(','),
      Action: 'InsertData'
    }]
  });

  const propId = (system, index) => fakeGuid(`${system.id}:prop:${index}`);

  const buildRecordRows = (system, record, index) => {
    const typeId = fakeGuid(`${system.id}:type`);
    const rows = [];

    if (system.mode === 'Snapshot') {
      rows.push({
        Id: fakeGuid(`${system.id}:data:${index}`),
        TypeId: typeId,
        Role: 2,
        ParentId: null,
        Value: `SNAP1:${JSON.stringify(record)}`,
        Version: 1
      });
      return rows;
    }

    const entityId = fakeGuid(`${system.id}:data:${index}`);
    rows.push({ Id: entityId, TypeId: typeId, Role: 2, ParentId: null, Value: null, Version: 1 });
    system.props.forEach((prop, propIndex) => {
      rows.push({
        Id: fakeGuid(`${system.id}:data:${index}:${propIndex}`),
        TypeId: propId(system, propIndex),
        Role: 2,
        ParentId: entityId,
        Value: String(record[prop.name] ?? ''),
        Version: 1
      });
    });
    return rows;
  };

  const buildRows = (system) => {
    const typeId = fakeGuid(`${system.id}:type`);
    const rows = [{
      Id: typeId,
      TypeId: META_TYPE,
      Role: 1,
      ParentId: null,
      Value: system.type,
      Version: 1
    }];

    system.props.forEach((prop, index) => {
      rows.push({
        Id: propId(system, index),
        TypeId: META_TYPE,
        Role: 1,
        ParentId: typeId,
        Value: `${prop.name}:${prop.type}`,
        Version: 1
      });
    });

    rows.push({
      Id: fakeGuid(`${system.id}:mode`),
      TypeId: META_ENGINE,
      Role: 4,
      ParentId: null,
      Value: `StorageMode=${system.mode}|TargetTypeId=${typeId}`,
      Version: 1
    });

    rows.push({
      Id: fakeGuid(`${system.id}:ui`),
      TypeId: META_ENGINE,
      Role: 8,
      ParentId: null,
      Value: `UI=${system.ui}|Type=Form|Entity=${system.type}|Fields=${system.fields.map((f) => `${f.name}:${f.control}`).join(',')}|Action=InsertData`,
      Version: 1
    });

    system.records.slice(0, 3).forEach((record, index) => {
      rows.push(...buildRecordRows(system, record, index));
    });

    return rows;
  };

  /* ---------------- demo state ---------------- */

  let currentSystem = null;
  let currentRows = [];
  let currentTab = 'schema';
  let runToken = 0;
  let rawCode = '';

  const els = {};

  const setStatus = (text, kind = '') => {
    if (!els.stageStatus) return;
    els.stageStatus.textContent = text;
    els.stageStatus.classList.toggle('is-running', kind === 'running');
    els.stageStatus.classList.toggle('is-done', kind === 'done');
  };

  const setActiveChip = (id) => {
    $$('.chip').forEach((chip) => {
      const active = chip.dataset.systemId === id;
      chip.classList.toggle('is-active', active);
      chip.setAttribute('aria-pressed', String(active));
    });
  };

  const renderCode = () => {
    if (!currentSystem) return;
    const value = currentTab === 'schema' ? systemJson(currentSystem) : currentRows;
    const text = JSON.stringify(value, null, 2);
    rawCode = text;
    els.code.innerHTML = highlightJson(text);
  };

  const poolChip = (row) => {
    const raw = row.Value === null || row.Value === undefined ? '—' : String(row.Value);
    const short = raw.length > 24 ? `${raw.slice(0, 24)}…` : raw;
    return `<span class="pool-row"><b>R${row.Role}</b><span class="val">${escapeHtml(short)}</span></span>`;
  };

  const appendPoolRow = (row) => {
    const wrap = document.createElement('span');
    wrap.innerHTML = poolChip(row);
    els.poolRows.appendChild(wrap.firstElementChild);
    els.poolCount.textContent = String(currentRows.length);
  };

  const showAllPool = () => {
    els.poolRows.innerHTML = '';
    currentRows.slice(0, 10).forEach((row) => {
      const node = document.createElement('span');
      node.innerHTML = poolChip(row);
      els.poolRows.appendChild(node.firstElementChild);
    });
    els.poolCount.textContent = String(currentRows.length);
  };

  const emptyAppHtml = () => `
    <div class="app-empty">
      <span class="app-empty-mark" aria-hidden="true">⌁</span>
      <p>等待一行元数据</p>
      <small>点击「生成系统」，这里会长出一个界面。</small>
    </div>`;

  const clearDemo = () => {
    els.poolRows.innerHTML = '';
    els.poolCount.textContent = '0';
    els.appBody.innerHTML = emptyAppHtml();
    els.appBody.classList.remove('is-grown');
    els.appUrl.textContent = 'genesis://parsing…';
    els.appCount.textContent = '0 rows';
    els.code.innerHTML = '';
  };

  const runSteps = [
    '解析意图 / prompt',
    '生成 SystemJson',
    '写入 Role=1 / 4 / 8',
    '重载 MetaCache',
    '渲染 Role=8 UI'
  ];

  const renderRunLog = () => {
    els.runLog.innerHTML = runSteps.map((step, index) => (
      `<div class="log-line"><span>0${index + 1}</span> ${escapeHtml(step)}</div>`
    )).join('');
  };

  const markLog = (index) => {
    Array.from(els.runLog.children).forEach((line, i) => {
      line.classList.toggle('is-active', i === index);
      line.classList.toggle('is-done', i < index);
    });
  };

  const markAllLogsDone = () => {
    Array.from(els.runLog.children).forEach((line) => {
      line.classList.remove('is-active');
      line.classList.add('is-done');
    });
  };

  const renderTable = (system) => {
    const table = $('#gen-table');
    if (!table) return;
    const records = recordsFor(system);
    const head = system.fields.map((f) => `<th>${escapeHtml(f.label)}</th>`).join('');
    const rows = records.map((record) => {
      const cells = system.fields.map((field) => {
        const value = record[field.name];
        if (field.name === 'Status') {
          return `<td><span class="status-pill" data-tone="${statusTone(value)}">${escapeHtml(value ?? '')}</span></td>`;
        }
        return `<td>${escapeHtml(value ?? '')}</td>`;
      }).join('');
      return `<tr>${cells}</tr>`;
    }).join('');

    table.innerHTML = records.length
      ? `<div class="gen-scroll"><table class="gen-table"><thead><tr>${head}</tr></thead><tbody>${rows}</tbody></table></div>`
      : '<div class="gen-empty">暂无数据，提交第一条记录。</div>';

    const count = $('#gen-count');
    if (count) count.textContent = String(records.length);
    els.appCount.textContent = `${records.length} record${records.length === 1 ? '' : 's'}`;
  };

  const statusTone = (value) => {
    const text = String(value ?? '');
    if (/已完成|已关闭|正常|在用|完成/.test(text)) return 'done';
    if (/处理中|维修|借出/.test(text)) return 'doing';
    return 'open';
  };

  const renderApp = (system) => {
    const fields = system.fields.map((field) => {
      const id = `field-${system.id}-${field.name}`;
      let control;
      if (field.control === 'Select') {
        const options = (field.options || []).map((option) => `<option>${escapeHtml(option)}</option>`).join('');
        control = `<select id="${id}" name="${escapeHtml(field.name)}">${options}</select>`;
      } else {
        control = `<input id="${id}" name="${escapeHtml(field.name)}" type="${field.type || 'text'}" placeholder="${escapeHtml(field.placeholder || '')}" autocomplete="off">`;
      }
      return `<div class="field"><label for="${id}">${escapeHtml(field.label)}</label>${control}</div>`;
    }).join('');

    els.appBody.innerHTML = `
      <div class="gen-head">
        <div>
          <h4>${escapeHtml(system.name)}</h4>
          <p>${escapeHtml(system.type)} · ${escapeHtml(system.ui)} · ${escapeHtml(system.mode)}</p>
        </div>
        <span class="gen-badge">Role=8 → UI</span>
      </div>
      <form class="gen-form" data-system-id="${system.id}" novalidate>
        ${fields}
        <button class="gen-submit" type="submit">InsertData <span aria-hidden="true">↗</span></button>
      </form>
      <div class="gen-table-wrap">
        <div class="gen-table-head"><span>DATA / Role=2</span><span><b id="gen-count">0</b> records</span></div>
        <div id="gen-table"></div>
      </div>`;

    els.appUrl.textContent = `genesis://${system.ui}`;
    els.appBody.classList.remove('is-grown');
    void els.appBody.offsetWidth;
    els.appBody.classList.add('is-grown');
    renderTable(system);
  };

  const resetSystem = (system) => {
    system.records = system.seed.map((record) => ({ ...record }));
  };

  const pickSystem = (text) => {
    const value = String(text || '');
    if (/图书|书籍|library|book/i.test(value)) return { system: SYSTEMS.book, matched: true };
    if (/工单|审批|ticket|issue|客服/i.test(value)) return { system: SYSTEMS.ticket, matched: true };
    if (/学生|班级|student|school/i.test(value)) return { system: SYSTEMS.student, matched: true };
    if (/设备|台账|资产|device|asset/i.test(value)) return { system: SYSTEMS.device, matched: true };
    return { system: currentSystem || SYSTEMS.book, matched: false };
  };

  const generate = async (system, animate = true) => {
    if (!system) return;
    const token = ++runToken;
    currentSystem = system;
    setActiveChip(system.id);
    resetSystem(system);
    currentRows = buildRows(system);
    clearDemo();
    setStatus(animate ? 'RUNNING' : 'SAMPLE', animate ? 'running' : 'done');
    els.generateBtn.disabled = true;
    renderRunLog();
    markLog(0);

    if (!animate) {
      renderCode();
      showAllPool();
      renderApp(system);
      markAllLogsDone();
      setStatus('SAMPLE', 'done');
      els.generateBtn.disabled = false;
      return;
    }

    await sleep(260);
    if (token !== runToken) return;
    markLog(1);
    setStatus('WRITING', 'running');
    renderCode();

    await sleep(340);
    if (token !== runToken) return;
    markLog(2);

    const visibleRows = currentRows.slice(0, 10);
    for (let i = 0; i < visibleRows.length; i += 1) {
      await sleep(75);
      if (token !== runToken) return;
      els.poolRows.insertAdjacentHTML('beforeend', poolChip(visibleRows[i]));
      els.poolCount.textContent = String(currentRows.length);
    }

    await sleep(180);
    if (token !== runToken) return;
    markLog(3);

    await sleep(280);
    if (token !== runToken) return;
    markLog(4);
    renderApp(system);
    markAllLogsDone();
    setStatus('LIVE', 'done');
    els.generateBtn.disabled = false;
    showToast(`系统已生成：${currentRows.length} 行 → ${system.ui}`);
  };

  /* ---------------- role / mode / engine / crypto ---------------- */

  const renderRole = (role) => {
    $$('.role-btn').forEach((button) => {
      const active = Number(button.dataset.role) === role.id;
      button.classList.toggle('is-active', active);
      button.setAttribute('aria-selected', String(active));
    });

    els.roleDetail.innerHTML = `
      <div class="role-desc">
        <div class="role-desc-top">
          <strong>${escapeHtml(role.name)} / ${escapeHtml(role.label)}</strong>
          ${role.deprecated
            ? '<span class="role-deprecated">deprecated</span>'
            : `<span>Role=${role.id}</span>`}
        </div>
        <p>${escapeHtml(role.desc)}</p>
      </div>
      <pre class="role-json">${miniJson(role.sample)}</pre>`;
  };

  const initRole = () => {
    els.roleRail.innerHTML = ROLES.map((role) => (
      `<button class="role-btn" type="button" role="tab" data-role="${role.id}" aria-selected="false" title="${escapeHtml(role.name)}">
        <b>${role.id}</b>${escapeHtml(role.label)}
      </button>`
    )).join('');

    els.roleRail.addEventListener('click', (event) => {
      const button = event.target.closest('.role-btn');
      if (!button) return;
      const role = ROLES.find((item) => item.id === Number(button.dataset.role));
      if (role) renderRole(role);
    });

    renderRole(ROLES[0]);
  };

  const modeNote = (mode) => (mode === 'graph'
    ? '1 实体行 + N 属性行：可遍历、可过滤、可逐字段更新；代价是更多行与更多指针。'
    : '1 行整对象：点查零拆解、零组装，schema 即类；代价是放弃属性级遍历。');

  const renderMode = (mode) => {
    $$('#mode-toggle button').forEach((button) => {
      button.classList.toggle('is-active', button.dataset.mode === mode);
      button.setAttribute('aria-pressed', String(button.dataset.mode === mode));
    });

    const book = { Title: '百年孤独', Author: '加西亚·马尔克斯', Year: 1967 };
    const object = `
      <div class="mode-object">
        <div class="mode-obj-head"><span>Book</span><span>CLR / dynamic</span></div>
        <div class="kv"><span>Title</span><span>百年孤独</span></div>
        <div class="kv"><span>Author</span><span>加西亚·马尔克斯</span></div>
        <div class="kv"><span>Year</span><span>1967</span></div>
      </div>`;

    let count;
    let rows;
    if (mode === 'snapshot') {
      count = '1';
      rows = `<div class="mini-row"><b>R2</b><span class="mt">SNAP1</span><span class="mv">${escapeHtml(JSON.stringify(book))}</span></div>`;
    } else {
      count = '1 + 3';
      rows = [
        `<div class="mini-row"><b>R2</b><span class="mt">entity</span><span class="mv">Book · ParentId=null</span></div>`,
        `<div class="mini-row"><b>R2</b><span class="mt">Title:String</span><span class="mv">百年孤独</span></div>`,
        `<div class="mini-row"><b>R2</b><span class="mt">Author:String</span><span class="mv">加西亚·马尔克斯</span></div>`,
        `<div class="mini-row"><b>R2</b><span class="mt">Year:Int</span><span class="mv">1967</span></div>`
      ].join('');
    }

    els.modeVisual.innerHTML = `
      <span class="mode-count">rows <b>${count}</b></span>
      ${object}
      <div class="mode-arrow">↓ ${mode === 'snapshot' ? 'serialize' : 'decompose'}</div>
      <div class="row-stack">${rows}</div>`;
    els.modeNote.textContent = modeNote(mode);
  };

  const initMode = () => {
    els.modeToggle.addEventListener('click', (event) => {
      const button = event.target.closest('button[data-mode]');
      if (!button) return;
      renderMode(button.dataset.mode);
    });
    renderMode('graph');
  };

  const renderEngine = (index) => {
    const engine = ENGINES[index];
    const p = (index / (ENGINES.length - 1)) * 100;
    els.engineRange.style.setProperty('--p', `${p}%`);
    els.engineRange.setAttribute('aria-valuetext', `${engine.name}，${engine.throughput} entities per second`);

    els.engineReadout.innerHTML = `
      <div class="engine-name">${escapeHtml(engine.name)}<small>${escapeHtml(engine.short)} · IPC ${engine.ipc}</small></div>
      <div class="engine-throughput">${formatNum(engine.throughput)}<small>entities / s</small></div>
      <div class="engine-stats">
        <span>写 <b>${formatNum(engine.write)} ms</b></span>
        <span>读 <b>${formatNum(engine.read)} ms</b></span>
        <span>总计 <b>${formatNum(engine.total)} ms</b></span>
        <span>ACID <b>${escapeHtml(engine.acid)}</b></span>
      </div>`;

    const bar = (label, value) => {
      const width = Math.max(2, (Math.log10(value + 1) / Math.log10(15420)) * 100);
      return `<div class="ebar"><span>${label}</span><span class="track"><span class="fill" style="--w:${width.toFixed(1)}%"></span></span><span class="num">${formatNum(value)} ms</span></div>`;
    };
    els.engineBars.innerHTML = bar('write', engine.write) + bar('read', engine.read);
  };

  const initEngine = () => {
    els.engineRange.addEventListener('input', () => renderEngine(Number(els.engineRange.value)));
    renderEngine(0);
  };

  const renderCrypto = (mode) => {
    $$('#crypto-toggle button').forEach((button) => {
      button.classList.toggle('is-active', button.dataset.crypto === mode);
      button.setAttribute('aria-pressed', String(button.dataset.crypto === mode));
    });

    const cipher = 'AGC1:9Xk2vQ7mR4tY8pL1nC6bV3sD0fG5hJ2kM9wE4rT7yU1iO8pA6sD3fG0hJ5kL2';
    const plain = '{"Name":"张三","City":"上海"}';
    const isAes = mode === 'aes';

    els.cryptoVisual.innerHTML = `
      <div class="crypto-block">
        <div class="crypto-label"><span>plaintext</span><span>C# object</span></div>
        <div class="crypto-plain">${escapeHtml(plain)}</div>
      </div>
      <div class="crypto-arrow">${isAes ? 'AES-GCM · tenant-AAD' : 'PLN1 · policy disabled'}</div>
      <div class="crypto-block">
        <div class="crypto-label"><span>stored value</span><span>${isAes ? 'AGC1' : 'PLN1'}</span></div>
        <div class="${isAes ? 'crypto-cipher' : 'crypto-plain'}">${escapeHtml(isAes ? cipher : `PLN1:${plain}`)}</div>
      </div>
      <div class="crypto-tenants">
        <div class="tenant"><b>Tenant A</b><span class="ok">✓ 解密成功</span></div>
        <div class="tenant"><b>Tenant B</b><span class="${isAes ? 'no' : 'ok'}">${isAes ? '✕ null / 不可见' : '✓ 可见（无隔离）'}</span></div>
      </div>`;

    els.cryptoNote.textContent = isAes
      ? 'Value 加密，结构仍然明文；换算法只改一行 Role=5 策略，历史数据按 AGC1/PLN1 前缀路由解密。'
      : '策略关闭 = 明文落库；PLN1 前缀仍把「本系统写入」与「外来裸数据」区分开。';
  };

  const initCrypto = () => {
    els.cryptoToggle.addEventListener('click', (event) => {
      const button = event.target.closest('button[data-crypto]');
      if (!button) return;
      renderCrypto(button.dataset.crypto);
    });
    renderCrypto('aes');
  };

  /* ---------------- charts ---------------- */

  const buildBars = (container, data, max, unit = '') => {
    container.innerHTML = data.map((item) => {
      const width = Math.max(3, (item.value / max) * 100);
      return `
        <div class="bar-row${item.best ? ' is-best' : ''}">
          <span class="bar-label">${escapeHtml(item.label)}</span>
          <span class="bar-track"><span class="bar-fill" data-w="${width.toFixed(1)}"></span></span>
          <span class="bar-num">${escapeHtml(item.display)}${unit}</span>
        </div>`;
    }).join('');
  };

  const revealBars = (container) => {
    $$('.bar-fill', container).forEach((bar) => {
      bar.style.width = `${bar.dataset.w}%`;
    });
  };

  const countUp = (el) => {
    const target = Number(el.dataset.count || 0);
    if (reduced) {
      el.textContent = String(target);
      return;
    }
    const start = performance.now();
    const duration = 1500;
    const tick = (now) => {
      const progress = Math.min(1, (now - start) / duration);
      const eased = 1 - (1 - progress) ** 3;
      el.textContent = String(Math.round(target * eased));
      if (progress < 1) requestAnimationFrame(tick);
    };
    requestAnimationFrame(tick);
  };

  const initCharts = () => {
    buildBars(els.throughputBars, THROUGHPUT, 72992);
    buildBars(els.reversalBars, REVERSAL, 154);

    const big = $('.big-number');
    if (!('IntersectionObserver' in window)) {
      $$('.chart-card').forEach((card) => revealBars(card));
      if (big) countUp(big);
      return;
    }

    const observer = new IntersectionObserver((entries) => {
      entries.forEach((entry) => {
        if (!entry.isIntersecting) return;
        revealBars(entry.target);
        observer.unobserve(entry.target);
      });
    }, { threshold: 0.35 });

    $$('.chart-card').forEach((card) => observer.observe(card));

    if (big) {
      const bigObserver = new IntersectionObserver((entries) => {
        entries.forEach((entry) => {
          if (!entry.isIntersecting) return;
          countUp(big);
          bigObserver.unobserve(entry.target);
        });
      }, { threshold: 0.5 });
      bigObserver.observe(big);
    }
  };

  /* ---------------- hero canvas ---------------- */

  const initCanvas = () => {
    if (reduced) return;
    const canvas = els.canvas;
    if (!canvas) return;
    const ctx = canvas.getContext('2d');
    if (!ctx) return;
    let width = 0;
    let height = 0;
    let dpr = 1;
    let particles = [];

    const makeParticles = () => {
      const count = window.innerWidth < 760 ? 26 : 58;
      particles = Array.from({ length: count }, () => ({
        x: Math.random() * width,
        y: Math.random() * height,
        vx: (Math.random() - 0.5) * 0.22,
        vy: (Math.random() - 0.5) * 0.22,
        r: Math.random() * 1.4 + 0.4
      }));
    };

    const resize = () => {
      dpr = Math.min(2, window.devicePixelRatio || 1);
      width = canvas.clientWidth;
      height = canvas.clientHeight;
      canvas.width = Math.max(1, Math.round(width * dpr));
      canvas.height = Math.max(1, Math.round(height * dpr));
      ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
      makeParticles();
    };

    const tick = () => {
      if (document.hidden) {
        requestAnimationFrame(tick);
        return;
      }
      ctx.clearRect(0, 0, width, height);

      for (let i = 0; i < particles.length; i += 1) {
        const p = particles[i];
        p.x += p.vx;
        p.y += p.vy;
        if (p.x < -20) p.x = width + 20;
        if (p.x > width + 20) p.x = -20;
        if (p.y < -20) p.y = height + 20;
        if (p.y > height + 20) p.y = -20;

        ctx.beginPath();
        ctx.arc(p.x, p.y, p.r, 0, Math.PI * 2);
        ctx.fillStyle = i % 5 === 0 ? 'rgba(184,242,74,0.45)' : 'rgba(110,231,216,0.28)';
        ctx.fill();

        for (let j = i + 1; j < particles.length; j += 1) {
          const q = particles[j];
          const dx = p.x - q.x;
          const dy = p.y - q.y;
          const distance = Math.hypot(dx, dy);
          if (distance > 128) continue;
          const alpha = (1 - distance / 128) * 0.08;
          ctx.beginPath();
          ctx.moveTo(p.x, p.y);
          ctx.lineTo(q.x, q.y);
          ctx.strokeStyle = `rgba(184,242,74,${alpha.toFixed(3)})`;
          ctx.lineWidth = 0.6;
          ctx.stroke();
        }
      }
      requestAnimationFrame(tick);
    };

    resize();
    window.addEventListener('resize', resize, { passive: true });
    requestAnimationFrame(tick);
  };

  const initHeroValue = () => {
    const el = $('.core-values .wide');
    if (!el || reduced) return;
    const values = ['{"Name":"张三"}', 'SNAP1:{"Name":"张三"}', 'AGC1:9Xk2vQ7m…', 'Role=6 → 旧值'];
    let index = 0;
    setInterval(() => {
      index = (index + 1) % values.length;
      el.textContent = values[index];
    }, 3200);
  };

  /* ---------------- nav / reveal / toast / copy ---------------- */

  const initNav = () => {
    const nav = els.nav;
    const update = () => nav.classList.toggle('is-scrolled', window.scrollY > 18);
    update();
    window.addEventListener('scroll', update, { passive: true });
  };

  const initReveal = () => {
    const items = $$('.reveal');
    if (reduced || !('IntersectionObserver' in window)) {
      items.forEach((item) => item.classList.add('in'));
      return;
    }
    const observer = new IntersectionObserver((entries) => {
      entries.forEach((entry) => {
        if (!entry.isIntersecting) return;
        entry.target.classList.add('in');
        observer.unobserve(entry.target);
      });
    }, { threshold: 0.12, rootMargin: '0px 0px -6% 0px' });

    items.forEach((item, index) => {
      item.style.setProperty('--i', String(index % 5));
      observer.observe(item);
    });
  };

  const toast = (message) => {
    els.toast.textContent = message;
    els.toast.classList.add('is-show');
    clearTimeout(toast.timer);
    toast.timer = setTimeout(() => els.toast.classList.remove('is-show'), 2400);
  };
  const showToast = toast;

  const initCopy = () => {
    els.copyBtn.addEventListener('click', async () => {
      if (!rawCode) return;
      try {
        await navigator.clipboard.writeText(rawCode);
        showToast('已复制到剪贴板');
      } catch {
        const area = document.createElement('textarea');
        area.value = rawCode;
        area.style.position = 'fixed';
        area.style.opacity = '0';
        document.body.appendChild(area);
        area.select();
        try {
          document.execCommand('copy');
          showToast('已复制到剪贴板');
        } catch {
          showToast('复制失败，请手动选择');
        }
        area.remove();
      }
    });
  };

  /* ---------------- demo init ---------------- */

  const initDemo = () => {
    els.promptChips.innerHTML = Object.values(SYSTEMS).map((system) => (
      `<button class="chip" type="button" data-system-id="${system.id}" aria-pressed="false">${escapeHtml(system.name)}</button>`
    )).join('');

    const runFromPrompt = () => {
      const picked = pickSystem(els.prompt.value);
      if (!picked.matched) showToast('演示只识别图书 / 工单 / 学生 / 设备四类场景，先用当前场景演示');
      generate(picked.system, true);
    };

    els.promptChips.addEventListener('click', (event) => {
      const chip = event.target.closest('.chip');
      if (!chip) return;
      const system = SYSTEMS[chip.dataset.systemId];
      if (system) {
        els.prompt.value = system.prompt;
        generate(system, true);
      }
    });

    els.generateBtn.addEventListener('click', runFromPrompt);

    els.prompt.addEventListener('keydown', (event) => {
      if ((event.metaKey || event.ctrlKey) && event.key === 'Enter') {
        event.preventDefault();
        runFromPrompt();
      }
    });

    $$('.tab').forEach((tab) => {
      tab.addEventListener('click', () => {
        currentTab = tab.dataset.tab;
        $$('.tab').forEach((item) => {
          const active = item === tab;
          item.classList.toggle('is-active', active);
          item.setAttribute('aria-selected', String(active));
        });
        renderCode();
      });
    });

    els.appBody.addEventListener('submit', (event) => {
      const form = event.target.closest('.gen-form');
      if (!form) return;
      event.preventDefault();
      const system = SYSTEMS[form.dataset.systemId];
      if (!system) return;

      const record = {};
      for (const field of system.fields) {
        const input = form.elements[field.name];
        if (!input) continue;
        let value = input.value.trim();
        if (!value) {
          showToast(`请填写「${field.label}」`);
          input.focus();
          return;
        }
        if (field.control === 'Number') {
          const number = Number(value);
          if (!Number.isFinite(number)) {
            showToast(`「${field.label}」需要是数字`);
            input.focus();
            return;
          }
          value = number;
        }
        record[field.name] = value;
      }

      system.records.push(record);
      renderTable(system);

      const newRows = buildRecordRows(system, record, system.records.length - 1);
      currentRows.push(...newRows);
      newRows.forEach((row, index) => {
        setTimeout(() => {
          els.poolRows.insertAdjacentHTML('beforeend', poolChip(row));
          els.poolCount.textContent = String(currentRows.length);
        }, index * 55);
      });
      if (currentTab === 'rows') renderCode();

      showToast(`已写入 1 条 ${system.type} · Role=2 × ${system.mode === 'Snapshot' ? 1 : 1 + system.props.length}`);
      form.reset();
    });

    const initial = SYSTEMS.book;
    els.prompt.value = initial.prompt;
    generate(initial, false);
  };

  /* ---------------- boot ---------------- */

  const init = () => {
    els.nav = $('#nav');
    els.canvas = $('#hero-canvas');
    els.roleRail = $('#role-rail');
    els.roleDetail = $('#role-detail');
    els.modeToggle = $('#mode-toggle');
    els.modeVisual = $('#mode-visual');
    els.modeNote = $('#mode-note');
    els.engineRange = $('#engine-range');
    els.engineReadout = $('#engine-readout');
    els.engineBars = $('#engine-bars');
    els.cryptoToggle = $('#crypto-toggle');
    els.cryptoVisual = $('#crypto-visual');
    els.cryptoNote = $('#crypto-note');
    els.prompt = $('#prompt-input');
    els.promptChips = $('#prompt-chips');
    els.generateBtn = $('#generate-btn');
    els.runLog = $('#run-log');
    els.stageStatus = $('#stage-status');
    els.code = $('#code-panel code');
    els.poolRows = $('#pool-rows');
    els.poolCount = $('#pool-count');
    els.appBody = $('#app-body');
    els.appUrl = $('#app-url');
    els.appCount = $('#app-count');
    els.copyBtn = $('#copy-btn');
    els.toast = $('#toast');
    els.throughputBars = $('#throughput-bars');
    els.reversalBars = $('#reversal-bars');

    initNav();
    initReveal();
    initCanvas();
    initHeroValue();
    initRole();
    initMode();
    initEngine();
    initCrypto();
    initCharts();
    initCopy();
    initDemo();
    window.__genesisReady = true;
  };

  if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', init, { once: true });
  } else {
    init();
  }
})();
