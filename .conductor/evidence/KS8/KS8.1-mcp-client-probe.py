import json, subprocess, sys, os

REPO = r"C:\code\conductor"
proc = subprocess.Popen(
    ["dotnet", "run", "--project", os.path.join(REPO, "src", "Conductor"), "--no-build", "--", "mcp-observe"],
    stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
    cwd=REPO, text=True, encoding="utf-8", bufsize=1)

def call(n, method, params=None, notify=False):
    msg = {"jsonrpc": "2.0", "method": method}
    if not notify: msg["id"] = n
    if params is not None: msg["params"] = params
    proc.stdin.write(json.dumps(msg) + "\n"); proc.stdin.flush()
    if notify: return None
    line = proc.stdout.readline()
    return json.loads(line) if line.strip() else None

out = []
def show(title, obj):
    out.append("### " + title)
    out.append(json.dumps(obj, indent=2)[:4000])
    out.append("")

r = call(1, "initialize", {"protocolVersion": "2025-06-18", "capabilities": {}, "clientInfo": {"name": "ks8.1-probe", "version": "1"}})
show("1. initialize -> capabilities carry resources and NOT tools", r)
call(None, "notifications/initialized", notify=True)

r = call(2, "resources/list")
res = r["result"]["resources"]
out.append("### 2. resources/list -> %d resources" % len(res))
out.append(json.dumps(res[:3], indent=2))
out.append("... (%d run resources follow)" % (len(res) - 1))
out.append("")

r = call(3, "resources/read", {"uri": "conductor://history"})
hist = json.loads(r["result"]["contents"][0]["text"])
out.append("### 3. resources/read conductor://history -> %d runs" % hist["count"])
out.append("%-10s %-22s %-14s %-12s %-10s %8s %6s" % ("shortId", "plan", "repo", "status", "stored", "cost", "sess"))
for run in hist["runs"]:
    out.append("%-10s %-22s %-14s %-12s %-10s %8.2f %6d" % (
        run["shortRunId"], run["plan"][:22], run["repo"][:14], run["status"], run["storedStatus"],
        run["costUsd"], run["sessions"]))
out.append("")
out.append("RECONCILED != STORED on these rows: " + ", ".join(
    "%s (%s -> %s)" % (r2["shortRunId"], r2["storedStatus"], r2["status"])
    for r2 in hist["runs"] if r2["shortRunId"] and r2["status"] != r2["storedStatus"]) or "none")
out.append("")

pick = next((r2 for r2 in hist["runs"] if r2["shortRunId"] and r2["status"] != r2["storedStatus"]), hist["runs"][0])
sid = pick["shortRunId"]
r = call(4, "resources/read", {"uri": "conductor://runs/%s/status" % sid})
st = json.loads(r["result"]["contents"][0]["text"])
show("4. resources/read conductor://runs/%s/status" % sid, {k: st[k] for k in
     ("runId", "plan", "status", "storedStatus", "storeLooksLive", "engine")} |
     {"state": {k: st["state"][k] for k in ("status", "stageId", "stageTitle", "doneCount", "totalCount", "totalCostUsd")}})

r = call(5, "resources/read", {"uri": "conductor://runs/%s/money" % sid})
mn = json.loads(r["result"]["contents"][0]["text"])
show("5. resources/read conductor://runs/%s/money -> billed only" % sid,
     {"scope": mn["scope"], "total": mn["total"], "categories": mn["categories"][:4]})

r = call(6, "tools/list")
show("6. tools/list -> the empty array", r)

for tool in ("task_update", "inject_instruction", "bg_start", "conductor_note", "bug_new", "run_query"):
    r = call(7, "tools/call", {"name": tool, "arguments": {}})
    out.append("### 7. tools/call %-20s -> %s" % (tool, json.dumps(r["error"])[:300]))
out.append("")

r = call(8, "resources/read", {"uri": "conductor://runs/%s/abort" % sid})
show("8. an invented write view is refused by name", r)

proc.stdin.close()
try: proc.wait(timeout=20)
except Exception: proc.kill()
err = proc.stderr.read()
out.append("### stderr from the server process (empty = the stdio wire was clean)")
out.append(repr(err[:500]))

sys.stdout.write("\n".join(out))
