export function normalizeColumnOrder(grid) {
  const names = (grid.columns || []).map(column => column.name);
  const known = new Set(names);
  const preserved = (grid.columnOrder || []).filter((name, index, order) => known.has(name) && order.indexOf(name) === index);
  const preservedSet = new Set(preserved);
  grid.columnOrder = [...preserved, ...names.filter(name => !preservedSet.has(name))];
  return grid.columnOrder;
}

export function orderedColumnEntries(grid) {
  normalizeColumnOrder(grid);
  const positions = new Map(grid.columnOrder.map((name, index) => [name, index]));
  return (grid.columns || []).map((column, index) => ({ c: column, i: index }))
    .sort((left, right) => positions.get(left.c.name) - positions.get(right.c.name));
}

export function reorderGridColumn(grid, sourceName, targetName, after = false) {
  if (!sourceName || sourceName === targetName) return false;
  normalizeColumnOrder(grid);
  const order = grid.columnOrder.filter(name => name !== sourceName);
  let targetIndex = order.indexOf(targetName);
  if (targetIndex < 0) return false;
  if (after) targetIndex++;
  order.splice(targetIndex, 0, sourceName);
  grid.columnOrder = order;
  return true;
}

export function gridSortState(grid, name) {
  if (!grid.orderBy) return null;
  const parts = grid.orderBy.split(",").map(value => value.trim()).filter(Boolean);
  for (let index = 0; index < parts.length; index++) {
    const match = parts[index].match(/^(.*?)\s+(ASC|DESC)(?:\s+NULLS\s+(?:FIRST|LAST))?$/i);
    if (!match) continue;
    const candidate = match[1].trim().replace(/^"|"$/g, "").replaceAll('""', '"');
    if (candidate === name) return { direction: match[2].toUpperCase(), priority: index + 1 };
  }
  return null;
}

export function cycleGridSort(grid, name, multi = false) {
  const existing = gridSortState(grid, name);
  const quoted = `"${name.replaceAll('"', '""')}"`;
  if (!multi) grid.orderBy = !existing ? `${quoted} ASC` : existing.direction === "ASC" ? `${quoted} DESC` : "";
  else {
    const parts = grid.orderBy ? grid.orderBy.split(",").map(value => value.trim()).filter(Boolean) : [];
    const at = existing ? existing.priority - 1 : -1;
    if (!existing) parts.push(`${quoted} ASC`);
    else if (existing.direction === "ASC") parts[at] = `${quoted} DESC`;
    else parts.splice(at, 1);
    grid.orderBy = parts.join(", ");
  }
  return grid.orderBy;
}

export function gridSelectionStatistics(selected, valueAt) {
  const coordinates = [...selected].map(key => key.split(":").map(Number));
  const values = coordinates.map(([row, column]) => valueAt(row, column)).filter(value => value != null);
  const numeric = values.map(Number).filter(Number.isFinite);
  const sum = numeric.reduce((total, value) => total + value, 0);
  return {
    cells: values.length,
    rows: new Set(coordinates.map(item => item[0])).size,
    columns: new Set(coordinates.map(item => item[1])).size,
    numeric: numeric.length,
    sum,
    average: numeric.length ? sum / numeric.length : null,
    minimum: numeric.length ? Math.min(...numeric) : null,
    maximum: numeric.length ? Math.max(...numeric) : null
  };
}

export function selectGridRange(grid, startRow, startColumn, endRow, endColumn, additive = false) {
  grid.selected ||= new Set();
  if (!additive) grid.selected.clear();
  for (let row = Math.min(startRow, endRow); row <= Math.max(startRow, endRow); row++)
    for (let column = Math.min(startColumn, endColumn); column <= Math.max(startColumn, endColumn); column++)
      grid.selected.add(`${row}:${column}`);
}
