// DOM form builders: widget settings (driven by widget.json's settings[]), plus the two
// hand-rolled editors for launch.json and events.json (their shape is fixed, not manifest-driven).

import { paletteSwatchField } from "./dom.js";

function el(tag, attrs, ...children) {
  const node = document.createElement(tag);
  if (attrs) {
    for (const [k, v] of Object.entries(attrs)) {
      if (k === "class") node.className = v;
      else if (k === "text") node.textContent = v;
      else if (k.startsWith("on") && typeof v === "function") node.addEventListener(k.slice(2), v);
      else if (v !== undefined && v !== null && v !== false) node.setAttribute(k, v === true ? "" : v);
    }
  }
  for (const c of children) {
    if (c === null || c === undefined) continue;
    node.append(c.nodeType ? c : document.createTextNode(String(c)));
  }
  return node;
}

/**
 * Render a form for a widget's settings[] schema into `container`.
 * `values` is the current saved object (may be partial/empty); defaults from the schema fill gaps.
 * Returns { getValue(): object } — call it right before saving.
 */
export function renderWidgetForm(container, schema, values, t) {
  container.innerHTML = "";
  const data = { ...(values || {}) };
  const grid = el("div", { class: "form-grid" });

  for (const field of schema || []) {
    if (data[field.key] === undefined) data[field.key] = field.default;
    grid.append(renderField(field, data, t));
  }
  container.append(grid);
  return { getValue: () => data };
}

// field.label / field.help may be a plain string (existing widgets, one language) or a
// { ru, en } object (see widgets/focus/widget.json) resolved against the settings window's
// current language (t.lang, set by i18n.js's makeT). Falls back to ru, then en, then undefined.
function localize(value, t) {
  if (value === undefined || value === null) return undefined;
  if (typeof value === "string") return value;
  const lang = (t && t.lang) || "ru";
  return value[lang] || value.ru || value.en || undefined;
}

function renderField(field, data, t) {
  const labelText = localize(field.label, t) || field.key;
  const label = el("label", { text: labelText });
  const help = field.help ? el("div", { class: "hint", text: localize(field.help, t) }) : null;
  const withHelp = (node) => { if (help) node.append(help); return node; };

  switch (field.type) {
    case "bool": {
      const mount = el("div");
      const row = el("div", { class: "setrow" },
        el("div", { class: "main" }, el("div", { class: "t", text: labelText }), help),
        el("div", { class: "row-control" }, mount));
      window.NNAUI.toggle(mount, { checked: !!data[field.key], onChange: (v) => { data[field.key] = v; } });
      return row;
    }
    case "select": {
      const mount = el("div");
      const options = (field.options || []).map((opt) => (typeof opt === "object" ? opt : { value: opt, label: opt }));
      window.NNAUI.select(mount, { value: data[field.key], options, onChange: (v) => { data[field.key] = v; } });
      return withHelp(el("div", { class: "ui-field" }, label, mount));
    }
    case "color": {
      const mount = el("div");
      paletteSwatchField(mount, t, normalizeHex(data[field.key]), (v) => { data[field.key] = v; });
      return withHelp(el("div", { class: "ui-field" }, label, mount));
    }
    case "path": {
      const input = el("input", { type: "text", value: data[field.key] || "", placeholder: t("formPath") });
      input.addEventListener("input", (e) => { data[field.key] = e.target.value; });
      const browse = el("button", { class: "ui-btn sm", type: "button", text: t("formBrowse") });
      return withHelp(el("div", { class: "ui-field" }, label, el("div", { class: "field-row" }, input, browse)));
    }
    case "timezone": {
      const listId = "tz-" + field.key + "-" + Math.random().toString(36).slice(2, 8);
      const input = el("input", { type: "text", value: data[field.key] || "", list: listId });
      input.addEventListener("input", (e) => { data[field.key] = e.target.value; });
      const datalist = el("datalist", { id: listId });
      try {
        for (const z of Intl.supportedValuesOf("timeZone")) datalist.append(el("option", { value: z }));
      } catch { /* older runtimes without supportedValuesOf */ }
      return withHelp(el("div", { class: "ui-field" }, label, input, datalist));
    }
    case "text": {
      const textarea = el("textarea", { text: data[field.key] || "" });
      textarea.addEventListener("input", (e) => { data[field.key] = e.target.value; });
      return withHelp(el("div", { class: "ui-field" }, label, textarea));
    }
    case "list":
      return renderListField(field, data, t);
    case "number": {
      const input = el("input", {
        type: "number", value: data[field.key] ?? 0,
        min: field.min, max: field.max, step: field.step || 1,
      });
      input.addEventListener("input", (e) => { data[field.key] = e.target.value === "" ? null : Number(e.target.value); });
      return withHelp(el("div", { class: "ui-field" }, label, input));
    }
    case "string":
    default: {
      const input = el("input", { type: "text", value: data[field.key] ?? "" });
      input.addEventListener("input", (e) => { data[field.key] = e.target.value; });
      return withHelp(el("div", { class: "ui-field" }, label, input));
    }
  }
}

function normalizeHex(v) {
  if (typeof v === "string" && /^#[0-9a-fA-F]{6}$/.test(v)) return v;
  return "#0B0B0B";
}

function renderListField(field, data, t) {
  const itemSchema = field.item || {};
  const columns = Object.keys(itemSchema);
  if (!Array.isArray(data[field.key])) data[field.key] = Array.isArray(field.default) ? field.default.map((x) => ({ ...x })) : [];
  const rows = data[field.key];

  const table = el("table", { class: "list-table" });
  const redraw = () => {
    table.innerHTML = "";
    const thead = el("tr", null, ...columns.map((c) => el("th", { text: c })), el("th", {}));
    table.append(thead);
    rows.forEach((row, idx) => {
      const cells = columns.map((c) => {
        const type = itemSchema[c];
        const input = el("input", { type: type === "number" ? "number" : "text", value: row[c] ?? "" });
        input.addEventListener("input", (e) => { row[c] = type === "number" ? Number(e.target.value) : e.target.value; });
        return el("td", null, input);
      });
      const removeBtn = el("button", { class: "ui-btn sm", type: "button", text: t("formRemove") });
      removeBtn.addEventListener("click", () => { rows.splice(idx, 1); redraw(); });
      table.append(el("tr", null, ...cells, el("td", null, removeBtn)));
    });
  };
  redraw();

  const addBtn = el("button", { class: "ui-btn sm", type: "button", text: t("formAdd") });
  addBtn.addEventListener("click", () => {
    const blank = {};
    for (const c of columns) blank[c] = itemSchema[c] === "number" ? 0 : "";
    rows.push(blank);
    redraw();
  });

  const help = field.help ? el("div", { class: "hint", text: localize(field.help, t) }) : null;
  return el("div", { class: "ui-field" }, el("label", { text: localize(field.label, t) || field.key }), help, table, addBtn);
}

// ── launch.json editor ──────────────────────────────────────────
// Shape: { groups: [{ id, label, items: [itemId, ...] }], items: { itemId: {label, open|cmd+args|aumid, badge} } }

export function renderLaunchForm(container, launchData, t) {
  container.innerHTML = "";
  const data = {
    groups: Array.isArray(launchData?.groups) ? launchData.groups.map((g) => ({ ...g, items: [...(g.items || [])] })) : [],
    items: { ...(launchData?.items || {}) },
  };

  const wrap = el("div", { class: "stack" });
  const redraw = () => {
    wrap.innerHTML = "";
    data.groups.forEach((group, gi) => wrap.append(renderGroup(group, gi)));
    const addGroupBtn = el("button", { class: "ui-btn", type: "button", text: t("addGroup") });
    addGroupBtn.addEventListener("click", () => {
      let n = 1;
      while (data.groups.some((g) => g.id === "group" + n)) n++;
      data.groups.push({ id: "group" + n, label: "GROUP " + n, items: [] });
      redraw();
    });
    wrap.append(addGroupBtn);
  };

  function renderGroup(group, gi) {
    const idInput = el("input", { type: "text", value: group.id });
    idInput.addEventListener("change", (e) => { group.id = e.target.value.trim() || group.id; });
    const labelInput = el("input", { type: "text", value: group.label });
    labelInput.addEventListener("input", (e) => { group.label = e.target.value; });

    const removeGroupBtn = el("button", { class: "ui-btn sm danger", type: "button", text: t("remove") });
    removeGroupBtn.addEventListener("click", () => { data.groups.splice(gi, 1); redraw(); });

    const itemsBox = el("div", { class: "stack" });
    const redrawItems = () => {
      itemsBox.innerHTML = "";
      group.items.forEach((itemId, ii) => itemsBox.append(renderItem(group, itemId, ii, redrawItems)));
      const addItemBtn = el("button", { class: "ui-btn sm", type: "button", text: t("addItem") });
      addItemBtn.addEventListener("click", () => {
        let n = 1;
        while (data.items["item" + n]) n++;
        const id = "item" + n;
        data.items[id] = { label: "ITEM " + n, open: "" };
        group.items.push(id);
        redrawItems();
      });
      itemsBox.append(addItemBtn);
    };
    redrawItems();

    return el("div", { class: "card stack" },
      el("div", { class: "field-row" },
        el("div", { class: "ui-field" }, el("label", { text: "id" }), idInput),
        el("div", { class: "ui-field" }, el("label", { text: t("groupLabel") }), labelInput),
        removeGroupBtn),
      itemsBox);
  }

  function renderItem(group, itemId, ii, redrawItems) {
    const item = data.items[itemId] || (data.items[itemId] = {});
    const kind = item.aumid ? "aumid" : item.cmd ? "cmd" : "open";

    const labelInput = el("input", { type: "text", value: item.label || "" });
    labelInput.addEventListener("input", (e) => { item.label = e.target.value; });

    const kindSelect = el("select");
    for (const k of ["open", "cmd", "aumid"]) kindSelect.append(el("option", { value: k, selected: k === kind }, k));

    const pathInput = el("input", {
      type: "text",
      value: kind === "aumid" ? (item.aumid || "") : kind === "cmd" ? (item.cmd || "") : (item.open || ""),
      placeholder: kind === "cmd" ? "cmd + args" : t("formPath"),
    });
    kindSelect.addEventListener("change", () => {
      delete item.open; delete item.cmd; delete item.aumid;
      pathInput.placeholder = kindSelect.value === "cmd" ? "cmd + args" : t("formPath");
      pathInput.value = "";
    });
    pathInput.addEventListener("input", (e) => {
      const k = kindSelect.value;
      delete item.open; delete item.cmd; delete item.aumid;
      item[k] = e.target.value;
    });

    const badgeInput = el("input", { type: "text", value: item.badge || "", placeholder: "badge" });
    badgeInput.addEventListener("input", (e) => { item.badge = e.target.value || undefined; });

    const removeBtn = el("button", { class: "ui-btn sm danger", type: "button", text: t("remove") });
    removeBtn.addEventListener("click", () => {
      group.items.splice(ii, 1);
      if (!data.groups.some((g) => g.items.includes(itemId))) delete data.items[itemId];
      redrawItems();
    });

    return el("div", { class: "field-row" }, labelInput, kindSelect, pathInput, badgeInput, removeBtn);
  }

  redraw();
  container.append(wrap);
  return {
    getValue: () => ({
      groups: data.groups.map((g) => ({ id: g.id, label: g.label, items: [...g.items] })),
      items: { ...data.items },
    }),
  };
}

// ── events.json editor ───────────────────────────────────────────
// Shape: { events: [{label, date}], daily: [{label, time}] }

export function renderEventsForm(container, eventsData, t) {
  container.innerHTML = "";
  const data = {
    events: Array.isArray(eventsData?.events) ? eventsData.events.map((e) => ({ ...e })) : [],
    daily: Array.isArray(eventsData?.daily) ? eventsData.daily.map((e) => ({ ...e })) : [],
  };

  function renderRows(list, dateOrTime, addLabel) {
    const box = el("div", { class: "stack" });
    const redraw = () => {
      box.innerHTML = "";
      list.forEach((row, idx) => {
        const labelInput = el("input", { type: "text", value: row.label || "" });
        labelInput.addEventListener("input", (e) => { row.label = e.target.value; });
        const valueInput = el("input", { type: dateOrTime, value: row[dateOrTime] || "" });
        valueInput.addEventListener("input", (e) => { row[dateOrTime] = e.target.value; });
        const removeBtn = el("button", { class: "ui-btn sm danger", type: "button", text: t("remove") });
        removeBtn.addEventListener("click", () => { list.splice(idx, 1); redraw(); });
        box.append(el("div", { class: "field-row" }, labelInput, valueInput, removeBtn));
      });
      const addBtn = el("button", { class: "ui-btn sm", type: "button", text: addLabel });
      addBtn.addEventListener("click", () => { list.push({ label: "", [dateOrTime]: "" }); redraw(); });
      box.append(addBtn);
    };
    redraw();
    return box;
  }

  const wrap = el("div", { class: "stack" },
    el("div", { class: "h-sec", text: t("eventOneTime") }),
    renderRows(data.events, "date", t("addEvent")),
    el("hr", { class: "hr" }),
    el("div", { class: "h-sec", text: t("eventDaily") }),
    renderRows(data.daily, "time", t("addEvent")));

  container.append(wrap);
  return { getValue: () => ({ events: data.events.map((e) => ({ ...e })), daily: data.daily.map((e) => ({ ...e })) }) };
}
