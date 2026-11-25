import { num } from "../format";

type Point = { day: string; prompt: number; completion: number; requests: number };

/** Stacked daily bars of prompt and completion tokens. Plain SVG, no chart library. */
export function UsageChart({ points }: { points: Point[] }) {
  if (points.length === 0) return null;
  const max = Math.max(1, ...points.map((p) => p.prompt + p.completion));
  const w = 720;
  const h = 160;
  const bw = Math.max(2, Math.min(28, w / points.length - 2));
  return (
    <div className="overflow-x-auto">
      <svg viewBox={`0 0 ${w} ${h + 20}`} className="w-full min-w-[480px]" role="img" aria-label="Tokens per day">
        {points.map((p, i) => {
          const x = (i * w) / points.length;
          const hp = (p.prompt / max) * h;
          const hc = (p.completion / max) * h;
          return (
            <g key={p.day}>
              <title>{`${p.day}: ${num(p.requests)} requests, ${num(p.prompt)} prompt, ${num(p.completion)} completion tokens`}</title>
              <rect x={x} y={h - hp - hc} width={bw} height={hc} fill="#f59e0b" />
              <rect x={x} y={h - hp} width={bw} height={hp} fill="#475569" />
              {(points.length <= 14 || i % Math.ceil(points.length / 10) === 0) && (
                <text x={x} y={h + 14} fontSize="9" fill="#64748b">
                  {p.day.slice(5)}
                </text>
              )}
            </g>
          );
        })}
      </svg>
      <div className="mt-1 flex gap-4 text-xs text-slate-400">
        <span>
          <span className="mr-1 inline-block h-2 w-2 bg-slate-600" />
          prompt tokens
        </span>
        <span>
          <span className="mr-1 inline-block h-2 w-2 bg-amber-500" />
          completion tokens
        </span>
      </div>
    </div>
  );
}

export function toPoints(rows: { day: string; promptTokens: number; completionTokens: number; requests: number }[], days: number) {
  const byDay = new Map<string, Point>();
  const today = new Date();
  for (let i = days - 1; i >= 0; i--) {
    const d = new Date(Date.UTC(today.getUTCFullYear(), today.getUTCMonth(), today.getUTCDate() - i));
    const key = d.toISOString().slice(0, 10);
    byDay.set(key, { day: key, prompt: 0, completion: 0, requests: 0 });
  }
  for (const r of rows) {
    const p = byDay.get(r.day);
    if (!p) continue;
    p.prompt += r.promptTokens;
    p.completion += r.completionTokens;
    p.requests += r.requests;
  }
  return [...byDay.values()];
}
