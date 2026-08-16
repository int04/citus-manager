const DB_NAME = "citus-manager-query-history";
const STORE = "queries";
const MAX_ITEMS = 5000;

function openDatabase() {
  return new Promise((resolve, reject) => {
    const request = indexedDB.open(DB_NAME, 1);
    request.onupgradeneeded = () => {
      const store = request.result.createObjectStore(STORE, { keyPath: "id", autoIncrement: true });
      store.createIndex("namespace", "namespace");
      store.createIndex("timestamp", "timestamp");
    };
    request.onsuccess = () => resolve(request.result);
    request.onerror = () => reject(request.error);
  });
}

function complete(transaction) {
  return new Promise((resolve, reject) => {
    transaction.oncomplete = resolve;
    transaction.onerror = () => reject(transaction.error);
    transaction.onabort = () => reject(transaction.error);
  });
}

export function createQueryHistory(namespace) {
  async function list(search = "") {
    const db = await openDatabase();
    const transaction = db.transaction(STORE, "readonly");
    const request = transaction.objectStore(STORE).getAll();
    const rows = await new Promise((resolve, reject) => { request.onsuccess = () => resolve(request.result); request.onerror = () => reject(request.error); });
    db.close();
    const needle = search.trim().toLocaleLowerCase();
    return rows.filter(x => x.namespace === namespace && (!needle || x.sql.toLocaleLowerCase().includes(needle) || (x.context || "").toLocaleLowerCase().includes(needle)))
      .sort((a, b) => b.timestamp - a.timestamp);
  }
  async function add(entry) {
    const db = await openDatabase();
    const transaction = db.transaction(STORE, "readwrite");
    transaction.objectStore(STORE).add({ ...entry, namespace, timestamp: entry.timestamp || Date.now() });
    await complete(transaction);
    const rows = await list();
    if (rows.length > MAX_ITEMS) {
      const cleanup = db.transaction(STORE, "readwrite");
      rows.slice(MAX_ITEMS).forEach(item => cleanup.objectStore(STORE).delete(item.id));
      await complete(cleanup);
    }
    db.close();
  }
  async function remove(id) {
    const db = await openDatabase(); const transaction = db.transaction(STORE, "readwrite");
    transaction.objectStore(STORE).delete(id); await complete(transaction); db.close();
  }
  async function clear() {
    const rows = await list(); const db = await openDatabase(); const transaction = db.transaction(STORE, "readwrite");
    rows.forEach(item => transaction.objectStore(STORE).delete(item.id)); await complete(transaction); db.close();
  }
  return { list, add, remove, clear };
}
