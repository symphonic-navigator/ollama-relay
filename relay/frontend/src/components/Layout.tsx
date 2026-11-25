import type { ReactNode } from "react";
import { NavLink } from "react-router";
import { logout, type User } from "../api";

const userLinks = [
  ["/models", "My models"],
  ["/keys", "API keys"],
  ["/usage", "My usage"],
  ["/account", "Account"],
] as const;

const adminLinks = [
  ["/admin/nodes", "Nodes"],
  ["/admin/models", "Publish"],
  ["/admin/users", "Users"],
  ["/admin/grants", "Grants"],
  ["/admin/usage", "Usage"],
  ["/admin/audit", "Audit"],
] as const;

function Item({ to, label }: { to: string; label: string }) {
  return (
    <NavLink
      to={to}
      className={({ isActive }) =>
        `block rounded px-3 py-1.5 text-sm ${isActive ? "bg-slate-800 text-amber-400" : "text-slate-300 hover:bg-slate-900"}`
      }
    >
      {label}
    </NavLink>
  );
}

export function Layout({ user, children }: { user: User; children: ReactNode }) {
  return (
    <div className="flex min-h-screen flex-col md:flex-row">
      <aside className="border-b border-slate-800 bg-slate-950 p-4 md:w-56 md:border-r md:border-b-0">
        <div className="mb-6 font-mono text-lg font-bold tracking-tight text-amber-400">
          ollama<span className="text-slate-500">/</span>relay
        </div>
        <nav className="space-y-0.5">
          {userLinks.map(([to, label]) => (
            <Item key={to} to={to} label={label} />
          ))}
        </nav>
        {user.isAdmin && (
          <>
            <div className="mt-6 mb-1 px-3 text-xs uppercase tracking-widest text-slate-500">Admin</div>
            <nav className="space-y-0.5">
              {adminLinks.map(([to, label]) => (
                <Item key={to} to={to} label={label} />
              ))}
            </nav>
          </>
        )}
        <div className="mt-8 border-t border-slate-800 pt-4 text-xs text-slate-500">
          Signed in as <span className="text-slate-300">{user.username}</span>
          <button className="mt-2 block text-amber-500 hover:underline" onClick={() => logout()}>
            Sign out
          </button>
        </div>
      </aside>
      <main className="min-w-0 flex-1 p-4 md:p-8">{children}</main>
    </div>
  );
}
