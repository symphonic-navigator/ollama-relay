import { useState } from "react";
import { api, type ApiKey } from "../api";
import { Empty, ErrorNote, SecretOnce } from "../components/Bits";
import { ago, dateTime } from "../format";
import { useResource } from "../hooks";

export function KeysPage() {
  const { data, error, reload } = useResource<ApiKey[]>("/mgmt/me/keys");
  const [name, setName] = useState("");
  const [created, setCreated] = useState<string | null>(null);
  const [actionError, setActionError] = useState<string | null>(null);

  return (
    <div className="space-y-4">
      <h1 className="h1">API keys</h1>
      <div className="card">
        <h2 className="h2">New key</h2>
        <form
          className="flex flex-wrap gap-2"
          onSubmit={async (e) => {
            e.preventDefault();
            setActionError(null);
            try {
              const res = await api.post<{ key: string }>("/mgmt/me/keys", { name });
              setCreated(res.key);
              setName("");
              reload();
            } catch (err) {
              setActionError((err as Error).message);
            }
          }}
        >
          <input className="input max-w-xs" placeholder="Name, e.g. open-webui" value={name} onChange={(e) => setName(e.target.value)} />
          <button className="btn-primary" disabled={!name.trim()}>Create</button>
        </form>
        {created && <SecretOnce title="Your new API key" value={created} />}
        <ErrorNote error={actionError ?? error} />
      </div>
      <div className="card">
        {data && data.length === 0 && <Empty>No keys yet.</Empty>}
        {data && data.length > 0 && (
          <table className="table">
            <thead>
              <tr>
                <th>Name</th>
                <th>Prefix</th>
                <th>Created</th>
                <th>Last used</th>
                <th />
              </tr>
            </thead>
            <tbody>
              {data.map((k) => (
                <tr key={k.id}>
                  <td>{k.name}</td>
                  <td className="font-mono text-xs">{k.prefix}_…</td>
                  <td title={dateTime(k.createdAt)}>{ago(k.createdAt)}</td>
                  <td title={dateTime(k.lastUsedAt)}>{ago(k.lastUsedAt)}</td>
                  <td className="text-right">
                    <button
                      className="btn-danger"
                      onClick={async () => {
                        if (!confirm(`Revoke key "${k.name}"? Running requests with it stop immediately.`)) return;
                        await api.del(`/mgmt/me/keys/${k.id}`);
                        reload();
                      }}
                    >
                      Revoke
                    </button>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
      </div>
    </div>
  );
}
