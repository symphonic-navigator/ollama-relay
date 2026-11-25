import { useState } from "react";
import { api, applySession, type Session } from "../api";
import { ErrorNote } from "../components/Bits";

export function PasswordForm({ onDone }: { onDone?: () => void }) {
  const [current, setCurrent] = useState("");
  const [next, setNext] = useState("");
  const [repeat, setRepeat] = useState("");
  const [error, setError] = useState<string | null>(null);
  const [ok, setOk] = useState(false);

  return (
    <form
      className="max-w-sm space-y-3"
      onSubmit={async (e) => {
        e.preventDefault();
        setError(null);
        setOk(false);
        if (next !== repeat) {
          setError("The new passwords do not match.");
          return;
        }
        try {
          applySession(await api.post<Session>("/mgmt/me/password", { currentPassword: current, newPassword: next }));
          setOk(true);
          setCurrent("");
          setNext("");
          setRepeat("");
          onDone?.();
        } catch (err) {
          setError((err as Error).message);
        }
      }}
    >
      <div>
        <label className="label" htmlFor="c">Current password</label>
        <input id="c" className="input" type="password" autoComplete="current-password" value={current} onChange={(e) => setCurrent(e.target.value)} />
      </div>
      <div>
        <label className="label" htmlFor="n">New password</label>
        <input id="n" className="input" type="password" autoComplete="new-password" value={next} onChange={(e) => setNext(e.target.value)} />
        <p className="mt-1 text-xs text-slate-500">At least 12 characters.</p>
      </div>
      <div>
        <label className="label" htmlFor="r">Repeat new password</label>
        <input id="r" className="input" type="password" autoComplete="new-password" value={repeat} onChange={(e) => setRepeat(e.target.value)} />
      </div>
      <ErrorNote error={error} />
      {ok && <div className="text-sm text-emerald-400">Password changed. Other sessions were signed out.</div>}
      <button className="btn-primary" disabled={!current || !next}>Change password</button>
    </form>
  );
}
