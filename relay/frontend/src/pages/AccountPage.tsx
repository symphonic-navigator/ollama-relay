import type { User } from "../api";
import { PasswordForm } from "./PasswordForm";

export function AccountPage({ user }: { user: User }) {
  return (
    <div>
      <h1 className="h1">Account</h1>
      <div className="card mb-4 text-sm">
        <div>
          Username: <span className="font-mono text-amber-400">{user.username}</span>
        </div>
        <div>Role: {user.isAdmin ? "admin" : "user"}</div>
      </div>
      <div className="card">
        <h2 className="h2">Change password</h2>
        <PasswordForm />
        <p className="mt-3 text-xs text-slate-500">Changing the password signs out your other browser sessions. API keys keep working.</p>
      </div>
    </div>
  );
}
