import { useState } from "react";
import type { UsageRow } from "../../api";
import { ErrorNote } from "../../components/Bits";
import { UsageChart, toPoints } from "../../components/UsageChart";
import { dateTime, num } from "../../format";
import { useResource } from "../../hooks";
import { DaysPicker, UsageTotals } from "../MyUsagePage";

type Recent = {
  startedAt: string;
  user: string;
  alias: string;
  node: string;
  kind: string;
  endpoint: string;
  queueWaitMs: number;
  timeToFirstByteMs: number | null;
  durationMs: number;
  promptTokens: number | null;
  completionTokens: number | null;
  outcome: string;
  status: number;
};

export function UsagePage() {
  const [days, setDays] = useState(30);
  const [user, setUser] = useState("");
  const [alias, setAlias] = useState("");
  const [node, setNode] = useState("");
  const all = useResource<UsageRow[]>(`/mgmt/admin/usage?days=${days}`);
  const recent = useResource<Recent[]>("/mgmt/admin/usage/recent?limit=50", 10000);

  const rows = (all.data ?? []).filter(
    (r) => (!user || r.userId === user) && (!alias || r.alias === alias) && (!node || r.nodeId === node),
  );
  const users = [...new Map((all.data ?? []).map((r) => [r.userId!, r.user!])).entries()];
  const aliases = [...new Set((all.data ?? []).map((r) => r.alias))].sort();
  const nodes = [...new Map((all.data ?? []).map((r) => [r.nodeId!, r.node!])).entries()];

  return (
    <div className="space-y-4">
      <div className="flex flex-wrap items-center justify-between gap-2">
        <h1 className="h1 mb-0">Usage</h1>
        <DaysPicker days={days} setDays={setDays} />
      </div>
      <ErrorNote error={all.error} />
      <div className="flex flex-wrap gap-2">
        <select className="input w-auto" value={user} onChange={(e) => setUser(e.target.value)}>
          <option value="">All users</option>
          {users.map(([id, name]) => (
            <option key={id} value={id}>{name}</option>
          ))}
        </select>
        <select className="input w-auto" value={alias} onChange={(e) => setAlias(e.target.value)}>
          <option value="">All models</option>
          {aliases.map((a) => (
            <option key={a}>{a}</option>
          ))}
        </select>
        <select className="input w-auto" value={node} onChange={(e) => setNode(e.target.value)}>
          <option value="">All nodes</option>
          {nodes.map(([id, name]) => (
            <option key={id} value={id}>{name}</option>
          ))}
        </select>
      </div>
      <div className="card">
        <UsageChart points={toPoints(rows, days)} />
      </div>
      <div className="grid gap-4 xl:grid-cols-2">
        <div className="card overflow-x-auto">
          <UsageTotals rows={rows} groupBy={(r) => r.user ?? "?"} label="User" />
        </div>
        <div className="card overflow-x-auto">
          <UsageTotals rows={rows} groupBy={(r) => r.alias} label="Model" />
        </div>
      </div>
      <div className="card overflow-x-auto">
        <h2 className="h2">Recent requests</h2>
        <p className="mb-2 text-xs text-slate-500">Metadata only, kept for 30 days. Daily totals are kept without limit.</p>
        <table className="table">
          <thead>
            <tr>
              <th>When</th>
              <th>User</th>
              <th>Model</th>
              <th>Node</th>
              <th>Kind</th>
              <th className="text-right">Wait</th>
              <th className="text-right">TTFB</th>
              <th className="text-right">Total</th>
              <th className="text-right">Tokens in/out</th>
              <th>Outcome</th>
            </tr>
          </thead>
          <tbody>
            {recent.data?.map((r, i) => (
              <tr key={i}>
                <td className="whitespace-nowrap">{dateTime(r.startedAt)}</td>
                <td>{r.user}</td>
                <td className="font-mono text-amber-300">{r.alias}</td>
                <td>{r.node}</td>
                <td>{r.kind}</td>
                <td className="text-right">{r.queueWaitMs} ms</td>
                <td className="text-right">{r.timeToFirstByteMs != null ? `${r.timeToFirstByteMs} ms` : "–"}</td>
                <td className="text-right">{num(r.durationMs)} ms</td>
                <td className="text-right">
                  {r.promptTokens ?? "–"} / {r.completionTokens ?? "–"}
                </td>
                <td className={r.outcome === "ok" ? "" : "text-red-400"}>
                  {r.outcome} {r.status ? `(${r.status})` : ""}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </div>
  );
}
