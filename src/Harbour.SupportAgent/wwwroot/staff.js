"use strict";

const $ = (id) => document.getElementById(id);
let key = "";

async function staffGet(path) {
  const response = await fetch(path, { headers: { "X-Staff-Key": key } });
  if (response.status === 401) throw new Error("That key was not accepted.");
  if (!response.ok) throw new Error(`Request failed with ${response.status}`);
  return response.json();
}

function span(className, text) {
  const node = document.createElement("span");
  node.className = className;
  node.textContent = text;
  return node;
}

async function showConversation(id) {
  const events = await staffGet(`/api/staff/conversations/${id}`);
  $("events").replaceChildren(...events.map((event) => {
    const item = document.createElement("li");
    item.className = `event ${event.kind}`;
    item.append(span("kind", event.kind.replace("_", " ")), span("detail", event.detail));
    return item;
  }));
}

async function load() {
  $("error").textContent = "";
  try {
    const conversations = await staffGet("/api/staff/conversations");
    $("conversations").replaceChildren(...conversations.map((c) => {
      const item = document.createElement("li");
      const button = document.createElement("button");
      button.type = "button";
      button.append(
        span("when", new Date(c.startedAt).toLocaleString()),
        span("counts", `${c.events} events${c.blocked ? `, ${c.blocked} guard` : ""}${c.handedOff ? ", handed off" : ""}`));
      button.addEventListener("click", () => showConversation(c.id).catch((e) => { $("error").textContent = e.message; }));
      item.append(button);
      return item;
    }));
  } catch (error) {
    $("error").textContent = error.message;
  }
}

$("key-form").addEventListener("submit", (event) => {
  event.preventDefault();
  key = $("key").value;
  load();
});
