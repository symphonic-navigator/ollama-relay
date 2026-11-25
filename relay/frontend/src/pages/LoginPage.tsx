import { useState } from "react";
import { login } from "../api";
import { ErrorNote } from "../components/Bits";

export function Shell({ title, children }: { title: string; children: React.ReactNode }) {
  return (
    <div className="flex min-h-screen items-center justify-center p-4">
      <div className="w-full max-w-sm">
        <div className="mb-6 text-center font-mono text-2xl font-bold tracking-tight text-amber-400">
          ollama<span className="text-slate-500">/</span>relay
        </div>
        <div className="card">
          <h1 className="h2">{title}</h1>
          {children}
        </div>
      </div>
    </div>
  );
}

export function LoginPage() {
  const [username, setUsername] = useState("");
  const [password, setPassword] = useState("");
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  return (
    <Shell title="Sign in">
      <form
        className="space-y-3"
        onSubmit={async (e) => {
          e.preventDefault();
          setBusy(true);
          setError(null);
          try {
            await login(username, password);
          } catch (err) {
            setError((err as Error).message);
          } finally {
            setBusy(false);
          }
        }}
      >
        <div>
          <label className="label" htmlFor="u">Username</label>
          <input id="u" className="input" autoComplete="username" value={username} onChange={(e) => setUsername(e.target.value)} />
        </div>
        <div>
          <label className="label" htmlFor="p">Password</label>
          <input id="p" className="input" type="password" autoComplete="current-password" value={password} onChange={(e) => setPassword(e.target.value)} />
        </div>
        <ErrorNote error={error} />
        <button className="btn-primary w-full justify-center" disabled={busy || !username || !password}>
          Sign in
        </button>
      </form>
      <p className="mt-4 text-xs text-slate-500">
        Accounts are invite only. Ask the relay admin for an invite link. Forgotten passwords are reset by the admin, too.
      </p>
    </Shell>
  );
}
