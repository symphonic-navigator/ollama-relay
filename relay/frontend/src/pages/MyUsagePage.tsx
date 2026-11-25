import { useState } from "react";
import type { UsageRow } from "../api";
import { ErrorNote } from "../components/Bits";
import { UsageChart, toPoints } from "../components/UsageChart";
import { duration, num } from "../format";
import { useResource } from "../hooks";

export function UsageTotals({ rows, groupBy, label }: { rows: UsageRow[]; groupBy: (r: UsageRow) => string; label: string }) {
  const groups = new Map<string, { requests: number; errors: number; prompt: number; completion: number; ms: number }>();
  for (const r of rows) {
    const key = groupBy(r);
    const g = groups.get(key) ?? { requests: 0, errors: 0, prompt: 0, completion: 0, ms: 0 };
    g.requests += r.requests;
    g.errors += r.errors;
    g.prompt += r.promptTokens;
    g.completion += r.completionTokens;
    g.ms += r.durationMs;
    groups.set(key, g);
  }
  const sorted = [...groups.entries()].sort((a, b) => b[1].prompt + b[1].completion - (a[1].prompt + a[1].completion));
  return (
    <table className="table">
      <thead>
        <tr>
          <th>{label}</th>
          <th className="text-right">Requests</th>
          <th className="text-right">Errors</th>
          <th className="text-right">Prompt tokens</th>
          <th className="text-right">Completion tokens</th>
          <th className="text-right">Busy time</th>
        </tr>
      </thead>
      <tbody>
        {sorted.map(([key, g]) => (
          <tr key={key}>
            <td className="font-mono">{key}</td>
            <td className="text-right">{num(g.requests)}</td>
            <td className="text-right">{num(g.errors)}</td>
            <td className="text-right">{num(g.prompt)}</td>
            <td className="text-right">{num(g.completion)}</td>
            <td className="text-right">{duration(g.ms)}</td>
          </tr>
        ))}
      </tbody>
    </table>
  );
}

export function DaysPicker({ days, setDays }: { days: number; setDays: (d: number) => void }) {
  return (
    <div className="flex gap-1">
      {[7, 30, 90, 365].map((d) => (
        <button key={d} className={d === days ? "btn-primary" : "btn"} onClick={() => setDays(d)}>
          {d}d
        </button>
      ))}
    </div>
  );
}

export function MyUsagePage() {
  const [days, setDays] = useState(30);
  const { data, error } = useResource<UsageRow[]>(`/mgmt/me/usage?days=${days}`);
  return (
    <div className="space-y-4">
      <div className="flex flex-wrap items-center justify-between gap-2">
        <h1 className="h1 mb-0">My usage</h1>
        <DaysPicker days={days} setDays={setDays} />
      </div>
      <ErrorNote error={error} />
      {data && (
        <>
          <div className="card">
            <UsageChart points={toPoints(data, days)} />
          </div>
          <div className="card">
            <UsageTotals rows={data} groupBy={(r) => r.alias} label="Model" />
          </div>
        </>
      )}
    </div>
  );
}
