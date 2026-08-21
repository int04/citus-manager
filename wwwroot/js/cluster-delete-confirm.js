"use strict";

const deleteForm = document.querySelector("#delete-cluster-form");
const deleteDialog = document.querySelector("#delete-cluster-dialog");
if (deleteForm && deleteDialog) {
  const close = () => deleteDialog.close();
  document.querySelector("[data-open-delete-cluster]")?.addEventListener("click", () => deleteDialog.showModal());
  deleteDialog.querySelectorAll("[data-close-delete-cluster]").forEach(button => button.addEventListener("click", close));
  deleteDialog.querySelector("[data-confirm-delete-cluster]")?.addEventListener("click", () => deleteForm.submit());
  deleteDialog.addEventListener("click", event => { if (event.target === deleteDialog) close(); });
}
