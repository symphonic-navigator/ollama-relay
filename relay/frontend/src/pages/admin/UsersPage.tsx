import { useState } from "react";
import { api, type AdminUser, type User } from "../../api";
import { CopyButton, ErrorNote } from "../../components/Bits";
import { ago, dateTime } from "../../format";
import { useResource } from "../../hooks";

type Invite = { id: string; createdAt: string; expiresAt: string; usedAt: string | null; usedBy: string | null; admin: boolean; status: string };

function LinkBox({ title, link, expiresAt }: { title: string; link: string; expiresAt: string }) {
  return (
    <div className="my-3 rounded-lg border border-amber-700 bg-amber-950/30 p-4">
      <div className="mb-2 text-sm font-medium text-amber-300">{title}</div>
      <div className="flex flex-wrap items-center gap-2">
        <code className="min-w-0 flex-1 break-all rounded bg-slate-950 px-2 py-1 font-mono text-xs select-all">{link}</code>
        <CopyButton text={link} />
      </div>
      <div className="mt-2 text-xs text-slate-400">
        Single use, valid until {dateTime(expiresAt)}. Send it by hand; the relay does not send e-mail.
      </div>
    </div>
  );
}

export function UsersPage({ self }: { self: User }) {
  const users = useResource<AdminUser[]>("/mgmt/admin/users");
  const invites = useResource<Invite[]>("/mgmt/admin/invites");
  const [link, setLink] = useState<{ title: string; link: string; expiresAt: string } | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [inviteAdmin, setInviteAdmin] = useState(false);

  const run = async (f: () => Promise<unknown>) => {
    setError(null);
    try {
      await f();
      users.reload();
      invites.reload();
    } catch (e) {
      setError((e as Error).message);
    }
  };

  return (
    <div className="space-y-4">
      <h1 className="h1">Users</h1>
      <ErrorNote error={error ?? users.error} />
      {link && <LinkBox {...link} />}
      <div className="card overflow-x-auto">
        <table className="table">
          <thead>
            <tr>
              <th>User</th>
              <th>Role</th>
              <th>Status</th>
              <th>Keys</th>
              <th>Last login</th>
              <th />
            </tr>
          </thead>
          <tbody>
            {users.data?.map((u) => (
              <tr key={u.id}>
                <td className="font-mono">{u.username}</td>
                <td>{u.isAdmin ? "admin" : "user"}</td>
                <td>
                  {u.disabled ? <span className="text-red-400">disabled</span> : "active"}
                  {u.mustChangePassword && <span className="ml-1 text-xs text-slate-500">(must change password)</span>}
                </td>
                <td>{u.apiKeys}</td>
                <td title={dateTime(u.lastLoginAt)}>{ago(u.lastLoginAt)}</td>
                <td>
                  {u.id !== self.id && (
                    <div className="flex flex-wrap justify-end gap-1">
                      <button className="btn py-0.5" onClick={() => run(() => api.patch(`/mgmt/admin/users/${u.id}`, { disabled: !u.disabled }))}>
                        {u.disabled ? "Enable" : "Disable"}
                      </button>
                      <button className="btn py-0.5" onClick={() => run(() => api.patch(`/mgmt/admin/users/${u.id}`, { isAdmin: !u.isAdmin }))}>
                        {u.isAdmin ? "Make user" : "Make admin"}
                      </button>
                      <button
                        className="btn py-0.5"
                        onClick={() =>
                          run(async () => {
                            const r = await api.post<{ link: string; expiresAt: string }>(`/mgmt/admin/users/${u.id}/reset`);
                            setLink({ title: `Password reset link for ${u.username}`, ...r });
                          })
                        }
                      >
                        Reset link
                      </button>
                      <button
                        className="btn-danger py-0.5"
                        onClick={() =>
                          confirm(`Delete ${u.username}? Account, keys, grants and sessions are removed. Usage history stays.`) &&
                          run(() => api.del(`/mgmt/admin/users/${u.id}`))
                        }
                      >
                        Delete
                      </button>
                    </div>
                  )}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>

      <div className="card">
        <div className="mb-3 flex flex-wrap items-center gap-3">
          <h2 className="h2 mb-0">Invites</h2>
          <label className="flex items-center gap-1 text-sm text-slate-300">
            <input type="checkbox" checked={inviteAdmin} onChange={(e) => setInviteAdmin(e.target.checked)} /> as admin
          </label>
          <button
            className="btn-primary"
            onClick={() =>
              run(async () => {
                const r = await api.post<{ link: string; expiresAt: string }>("/mgmt/admin/invites", { admin: inviteAdmin });
                setLink({ title: "Invite link", ...r });
              })
            }
          >
            New invite link
          </button>
        </div>
        <table className="table">
          <thead>
            <tr>
              <th>Created</th>
              <th>Status</th>
              <th>Role</th>
              <th>Used by</th>
              <th />
            </tr>
          </thead>
          <tbody>
            {invites.data?.map((i) => (
              <tr key={i.id}>
                <td title={dateTime(i.createdAt)}>{ago(i.createdAt)}</td>
                <td>{i.status}</td>
                <td>{i.admin ? "admin" : "user"}</td>
                <td>{i.usedBy ?? "–"}</td>
                <td className="text-right">
                  {i.status === "open" && (
                    <button className="btn-danger py-0.5" onClick={() => run(() => api.del(`/mgmt/admin/invites/${i.id}`))}>
                      Revoke
                    </button>
                  )}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </div>
  );
}
