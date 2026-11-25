import type { User } from "../api";
import { logout } from "../api";
import { Shell } from "./LoginPage";
import { PasswordForm } from "./PasswordForm";

export function ForcedPasswordPage({ user }: { user: User }) {
  return (
    <Shell title="Set your own password">
      <p className="mb-3 text-sm text-slate-300">
        Hi <span className="font-mono text-amber-400">{user.username}</span>, please replace the initial password before you continue.
      </p>
      <PasswordForm />
      <button className="mt-4 text-xs text-slate-500 hover:underline" onClick={() => logout()}>
        Sign out
      </button>
    </Shell>
  );
}
