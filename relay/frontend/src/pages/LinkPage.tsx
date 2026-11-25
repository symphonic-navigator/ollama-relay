import { useEffect, useState } from "react";
import { useNavigate } from "react-router";
import { api, applySession, type Session } from "../api";
import { ErrorNote } from "../components/Bits";
import { Shell } from "./LoginPage";

/** Invite and password reset links carry their token in the URL fragment, which never reaches a server log. */
export function LinkPage({ kind }: { kind: "invite" | "reset" }) {
  const token = window.location.hash.replace(/^#/, "");
  const navigate = useNavigate();
  const [state, setState] = useState<"checking" | "ok" | "invalid">("checking");
  const [resetFor, setResetFor] = useState<string | null>(null);
  const [username, setUsername] = useState("");
  const [password, setPassword] = useState("");
  const [repeat, setRepeat] = useState("");
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    if (!token) {
      setState("invalid");
      return;
    }
    api
      .post<{ kind: string; username: string | null }>("/mgmt/auth/link/check", { token })
      .then((r) => {
        setState(r.kind === kind ? "ok" : "invalid");
        setResetFor(r.username);
      })
      .catch(() => setState("invalid"));
  }, [token, kind]);

  const title = kind === "invite" ? "Create your account" : "Choose a new password";
  if (state === "checking") return <Shell title={title}><p className="text-sm text-slate-400">Checking link…</p></Shell>;
  if (state === "invalid")
    return (
      <Shell title={title}>
        <p className="text-sm text-slate-300">This link is invalid, used or expired. Ask the admin for a new one.</p>
      </Shell>
    );

  return (
    <Shell title={title}>
      <form
        className="space-y-3"
        onSubmit={async (e) => {
          e.preventDefault();
          setError(null);
          if (password !== repeat) {
            setError("The passwords do not match.");
            return;
          }
          try {
            const res =
              kind === "invite"
                ? await api.post<Session>("/mgmt/auth/invite/redeem", { token, username, password })
                : await api.post<Session>("/mgmt/auth/reset/redeem", { token, password });
            history.replaceState(null, "", "/");
            if ("accessToken" in res) applySession(res);
            navigate("/models", { replace: true });
          } catch (err) {
            setError((err as Error).message);
          }
        }}
      >
        {kind === "invite" ? (
          <div>
            <label className="label" htmlFor="u">Username</label>
            <input id="u" className="input" autoComplete="username" value={username} onChange={(e) => setUsername(e.target.value)} />
            <p className="mt-1 text-xs text-slate-500">3 to 32 characters: letters, digits, dot, dash, underscore.</p>
          </div>
        ) : (
          <p className="text-sm text-slate-300">
            Account: <span className="font-mono text-amber-400">{resetFor}</span>
          </p>
        )}
        <div>
          <label className="label" htmlFor="p">Password</label>
          <input id="p" className="input" type="password" autoComplete="new-password" value={password} onChange={(e) => setPassword(e.target.value)} />
          <p className="mt-1 text-xs text-slate-500">At least 12 characters. A passphrase works well.</p>
        </div>
        <div>
          <label className="label" htmlFor="r">Repeat password</label>
          <input id="r" className="input" type="password" autoComplete="new-password" value={repeat} onChange={(e) => setRepeat(e.target.value)} />
        </div>
        <ErrorNote error={error} />
        <button className="btn-primary w-full justify-center" disabled={!password || (kind === "invite" && !username)}>
          {kind === "invite" ? "Create account" : "Set password"}
        </button>
      </form>
    </Shell>
  );
}
