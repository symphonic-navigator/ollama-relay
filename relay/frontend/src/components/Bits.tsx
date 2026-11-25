import { useState, type ReactNode } from "react";

export function ErrorNote({ error }: { error: string | null | undefined }) {
  if (!error) return null;
  return <div className="my-2 rounded border border-red-900 bg-red-950/60 px-3 py-2 text-sm text-red-200">{error}</div>;
}

export function Dot({ on, title }: { on: boolean; title?: string }) {
  return (
    <span
      title={title}
      className={`inline-block h-2 w-2 rounded-full ${on ? "bg-emerald-400 shadow-[0_0_6px] shadow-emerald-400" : "bg-slate-600"}`}
    />
  );
}

export function CopyButton({ text, label = "Copy" }: { text: string; label?: string }) {
  const [done, setDone] = useState(false);
  return (
    <button
      className="btn"
      onClick={async () => {
        try {
          await navigator.clipboard.writeText(text);
          setDone(true);
          setTimeout(() => setDone(false), 1500);
        } catch {
          /* clipboard needs a secure context; the value is selectable anyway */
        }
      }}
    >
      {done ? "Copied" : label}
    </button>
  );
}

/** A secret that is shown exactly once. */
export function SecretOnce({ title, value, children }: { title: string; value: string; children?: ReactNode }) {
  return (
    <div className="my-3 rounded-lg border border-amber-700 bg-amber-950/30 p-4">
      <div className="mb-2 text-sm font-medium text-amber-300">{title}</div>
      <div className="flex flex-wrap items-center gap-2">
        <code className="min-w-0 flex-1 break-all rounded bg-slate-950 px-2 py-1 font-mono text-xs select-all">{value}</code>
        <CopyButton text={value} />
      </div>
      <div className="mt-2 text-xs text-slate-400">
        This is shown only once. Store it now; the relay keeps only a hash.
      </div>
      {children}
    </div>
  );
}

export function Empty({ children }: { children: ReactNode }) {
  return <div className="py-6 text-center text-sm text-slate-500">{children}</div>;
}
