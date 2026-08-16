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
  constructor(status, title) { super(); this.status = status; this.title = title || status; }
  eq(other) { return this.status === other.status && this.title === other.title; }
  toDOM() {
    const node = document.createElement("i");
    const icons = { queued: "fa-clock-o", running: "fa-spinner fa-spin", success: "fa-check-circle", error: "fa-times-circle", skipped: "fa-minus-circle" };
    node.className = `fa ${icons[this.status] || "fa-circle-o"} cm-query-status-${this.status}`;
    node.title = this.title; node.setAttribute("aria-label", this.title);
    return node;
  }
}

function statusGutter() {
  return gutter({
    class: "cm-query-status-gutter",
    markers(view) {
      const ranges = [];
      for (const item of view.state.field(marksField)) {
        const line = view.state.doc.line(Math.max(1, Math.min(view.state.doc.lines, item.line)));
        ranges.push(new StatusMarker(item.status, item.title).range(line.from));
      }
      return RangeSet.of(ranges.sort((left, right) => left.from - right.from));
    },
    initialSpacer: () => new StatusMarker("queued", "Trạng thái statement")
  });
}

function completionSource(context, values) {
  const word = context.matchBefore(/[\w$.]+/);
  if (!word && !context.explicit) return null;
  return { from: word ? word.from : context.pos, options: values.map(item => typeof item === "string" ? { label: item, type: "keyword" } : item), validFor: /^[\w$.]*$/ };
}

const editorTheme = EditorView.theme({
  "&": { height: "100%", fontSize: "14px", backgroundColor: "transparent" },
  ".cm-scroller": { fontFamily: "var(--font-mono, 'Cascadia Code', Consolas, monospace)", lineHeight: "1.65", overflow: "auto" },
  ".cm-content": { padding: "12px 0", caretColor: "#38bdf8" },
  ".cm-gutters": { backgroundColor: "var(--qc-toolbar)", borderRight: "1px solid var(--qc-border)", color: "var(--qc-muted)" },
  ".cm-activeLine, .cm-activeLineGutter": { backgroundColor: "var(--qc-active-line)" },
  ".cm-selectionBackground, ::selection": { backgroundColor: "var(--qc-selection) !important" },
  ".cm-tooltip-autocomplete": { border: "1px solid var(--qc-border-strong)", backgroundColor: "var(--qc-popup)", color: "var(--qc-text)" },
  ".cm-tooltip-autocomplete > ul > li[aria-selected]": { backgroundColor: "var(--qc-selection-strong)", color: "white" },
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
        basicSetup, sql({ dialect: PostgreSQL }), marksField, statusGutter(), editorTheme,
        completionCompartment.of(autocompletion({ override: [context => completionSource(context, completionValues)], activateOnTyping: true })),
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
    setCompletions(values) { completionValues = values || []; view.dispatch({ effects: completionCompartment.reconfigure(autocompletion({ override: [context => completionSource(context, completionValues)] })) }); },
    setStatuses(items) { view.dispatch({ effects: setMarks.of(items || []) }); },
    focus: () => view.focus(),
    destroy: () => view.destroy()
  };
}

window.CitusQueryEditor = { create };
export { create };
