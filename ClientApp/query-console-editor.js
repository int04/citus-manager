import { basicSetup } from "codemirror";
import { EditorState, StateEffect, StateField, Compartment, RangeSet } from "@codemirror/state";
import { EditorView, keymap, gutter, GutterMarker } from "@codemirror/view";
import { sql, PostgreSQL } from "@codemirror/lang-sql";
import { autocompletion, completionKeymap } from "@codemirror/autocomplete";
import { indentWithTab } from "@codemirror/commands";

const setMarks = StateEffect.define();
const marksField = StateField.define({
  create: () => [],
  update(value, transaction) {
    for (const effect of transaction.effects) if (effect.is(setMarks)) return effect.value;
    return value;
  }
});

class StatusMarker extends GutterMarker {
  constructor(status, title, statementIndex, onSkip) { super(); this.status = status; this.title = title || status; this.statementIndex = statementIndex; this.onSkip = onSkip; }
  eq(other) { return this.status === other.status && this.title === other.title && this.statementIndex === other.statementIndex; }
  toDOM() {
    if (this.status === "queued" && Number.isInteger(this.statementIndex) && this.onSkip) {
      const button = document.createElement("button");
      button.type = "button";
      button.className = "cm-query-status-cancel";
      button.title = this.title;
      button.setAttribute("aria-label", this.title);
      button.innerHTML = '<i class="fa fa-stop-circle" aria-hidden="true"></i>';
      button.onclick = event => { event.preventDefault(); event.stopPropagation(); this.onSkip(this.statementIndex); };
      return button;
    }
    const icon = document.createElement("i");
    const icons = { running: "fa-spinner fa-spin", success: "fa-check-circle", error: "fa-times-circle", skipped: "fa-minus-circle", queued: "fa-circle-o" };
    icon.className = `fa ${icons[this.status] || "fa-circle-o"} cm-query-status-${this.status}`;
    icon.title = this.title; icon.setAttribute("aria-label", this.title);
    return icon;
  }
}

function statusGutter(onSkip) {
  return gutter({
    class: "cm-query-status-gutter",
    markers(view) {
      const ranges = [];
      for (const item of view.state.field(marksField)) {
        const line = view.state.doc.line(Math.max(1, Math.min(view.state.doc.lines, item.line)));
        ranges.push(new StatusMarker(item.status, item.title, item.statementIndex, onSkip).range(line.from));
      }
      return RangeSet.of(ranges.sort((left, right) => left.from - right.from));
    },
    initialSpacer: () => new StatusMarker("queued", "Trạng thái statement")
  });
}

function completionSource(context, values) {
  const word = context.matchBefore(/[\w$.]+/);
  if (!word && !context.explicit) return null;
  const sqlBeforeCursor = context.state.doc.sliceString(0, context.pos);
  const currentStatement = sqlBeforeCursor.slice(sqlBeforeCursor.lastIndexOf(";") + 1);
  const usedRelations = new Set();
  const relationPattern = /\b(?:FROM|JOIN)\s+((?:"(?:[^"]|"")*"|[A-Za-z_][\w$]*)(?:\s*\.\s*(?:"(?:[^"]|"")*"|[A-Za-z_][\w$]*))?)/gi;
  for (const match of currentStatement.matchAll(relationPattern)) {
    const relation = match[1].replaceAll(/\s+/g, "").replaceAll('"', "").toLowerCase();
    usedRelations.add(relation);
    usedRelations.add(relation.split(".").at(-1));
  }
  const options = values
    .map(item => typeof item === "string" ? { label: item, type: "keyword" } : item)
    .filter(item => !item.joinSource ||
      (item.joinSource.some(source => usedRelations.has(source)) &&
       !item.joinTarget.some(target => usedRelations.has(target))));
  return { from: word ? word.from : context.pos, options, validFor: /^[\w$.]*$/ };
}

function completionIcon(completion) {
  const icons = {
    keyword: "fa-code", namespace: "fa-folder-open", class: "fa-table",
    property: "fa-columns", function: "fa-bolt", type: "fa-cube"
  };
  const icon = document.createElement("i");
  icon.className = `fa ${icons[completion.type] || "fa-circle-o"} cm-query-completion-icon is-${completion.type || "other"}`;
  icon.setAttribute("aria-hidden", "true");
  return icon;
}

const completionExtension = values => autocompletion({
  override: [context => completionSource(context, values)],
  activateOnTyping: true,
  addToOptions: [{ render: completionIcon, position: 20 }]
});

const editorTheme = EditorView.theme({
  "&": { height: "100%", fontSize: "14px", backgroundColor: "transparent" },
  ".cm-scroller": { fontFamily: "var(--font-mono, 'Cascadia Code', Consolas, monospace)", lineHeight: "1.65", overflow: "auto" },
  ".cm-content": { padding: "12px 0", caretColor: "var(--qc-caret)", userSelect: "text", WebkitUserSelect: "text", cursor: "text" },
  ".cm-line": { userSelect: "text", WebkitUserSelect: "text" },
  ".cm-gutters": { backgroundColor: "var(--qc-toolbar)", borderRight: "1px solid var(--qc-border)", color: "var(--qc-muted)" },
  ".cm-activeLine, .cm-activeLineGutter": { backgroundColor: "var(--qc-active-line)" },
  ".cm-cursor, .cm-dropCursor": { borderLeftColor: "var(--qc-caret)", borderLeftWidth: "2px" },
  ".cm-selectionLayer .cm-selectionBackground": { backgroundColor: "var(--qc-editor-selection) !important" },
  "&.cm-focused > .cm-scroller > .cm-selectionLayer .cm-selectionBackground": { backgroundColor: "var(--qc-editor-selection) !important" },
  ".cm-content ::selection": { backgroundColor: "var(--qc-editor-selection-native) !important", color: "var(--qc-editor-selection-text) !important" },
  ".cm-tooltip-autocomplete": { border: "1px solid var(--qc-border-strong)", backgroundColor: "var(--qc-popup)", color: "var(--qc-text)" },
  ".cm-tooltip-autocomplete > ul > li[aria-selected]": { backgroundColor: "var(--qc-selection-strong)", color: "white" },
  ".cm-completionIcon": { display: "none" },
  ".cm-query-completion-icon": { width: "16px", marginRight: "7px", textAlign: "center", color: "var(--qc-muted)" },
  ".cm-query-completion-icon.is-class": { color: "#22b8a7" },
  ".cm-query-completion-icon.is-property": { color: "#e87979" },
  ".cm-query-completion-icon.is-namespace": { color: "#eab308" },
  ".cm-query-completion-icon.is-function": { color: "#a78bfa" },
  ".cm-query-completion-icon.is-keyword": { color: "#60a5fa" },
  ".cm-query-status-running": { color: "#38bdf8" },
  ".cm-query-status-success": { color: "#22c55e" },
  ".cm-query-status-error": { color: "#ef4444" },
  ".cm-query-status-skipped, .cm-query-status-queued": { color: "#94a3b8" }
}, { dark: true });

function create(options) {
  let completionValues = options.completions || [];
  const completionCompartment = new Compartment();
  const view = new EditorView({
    parent: options.parent,
    state: EditorState.create({
      doc: options.value || "",
      extensions: [
        basicSetup, sql({ dialect: PostgreSQL }), marksField, statusGutter(options.onSkipStatement), editorTheme,
        completionCompartment.of(completionExtension(completionValues)),
        keymap.of([{ key: "Ctrl-Enter", preventDefault: true, run: () => { options.onRun?.(); return true; } }, indentWithTab, ...completionKeymap]),
        EditorView.updateListener.of(update => { if (update.docChanged) options.onChange?.(update.state.doc.toString()); })
      ]
    })
  });
  return {
    view,
    getValue: () => view.state.doc.toString(),
    setValue(value) { view.dispatch({ changes: { from: 0, to: view.state.doc.length, insert: value || "" } }); },
    getSelection() { const range = view.state.selection.main; return { from: range.from, to: range.to, empty: range.empty, cursor: range.head }; },
    setCompletions(values) { completionValues = values || []; view.dispatch({ effects: completionCompartment.reconfigure(completionExtension(completionValues)) }); },
    setStatuses(items) { view.dispatch({ effects: setMarks.of(items || []) }); },
    focus: () => view.focus(),
    destroy: () => view.destroy()
  };
}

window.CitusQueryEditor = { create };
export { create };
