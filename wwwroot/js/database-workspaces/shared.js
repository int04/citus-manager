export const html = value => String(value ?? "").replace(/[&<>"']/g, character => ({
  "&": "&amp;",
  "<": "&lt;",
  ">": "&gt;",
  '"': "&quot;",
  "'": "&#39;"
})[character]);

export async function problem(response) {
  try {
    const body = await response.json();
    return body.detail || body.title || "Database request failed.";
  } catch {
    return "Database request failed.";
  }
}

export function createJsonApi(token) {
  return async (url, body, signal) => {
    const response = await fetch(url, {
      method: "POST",
      headers: { "Content-Type": "application/json", "RequestVerificationToken": token },
      body: JSON.stringify(body),
      signal
    });
    if (!response.ok) throw new Error(await problem(response));
    return response.json();
  };
}
