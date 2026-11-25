import { useState } from "react";
import { api, type NodeInfo, type NodesResponse } from "../../api";
import { Dot, Empty, ErrorNote, SecretOnce } from "../../components/Bits";
import { ago, bytes, until } from "../../format";
import { useResource } from "../../hooks";

function TokenHelp({ token }: { token: string }) {
  const relay = window.location.origin.replace(/^http/, "ws");
  return (
    <SecretOnce title="Node token" value={token}>
      <p className="mt-3 text-xs text-slate-400">Put this into the node's <code>.env</code> and start the agent:</p>
      <pre className="mt-1 overflow-x-auto rounded bg-slate-950 p-2 font-mono text-xs">{`RELAY_URL=${relay}/node/connect
NODE_TOKEN=${token}
OLLAMA_URL=http://127.0.0.1:11434`}</pre>
    </SecretOnce>
  );
}

function NodeCard({ node, onChange, onToken }: { node: NodeInfo; onChange: () => void; onToken: (t: string) => void }) {
  const [max, setMax] = useState(String(node.maxConcurrency));
  const [error, setError] = useState<string | null>(null);
  const run = async (f: () => Promise<unknown>) => {
    setError(null);
    try {
      await f();
      onChange();
    } catch (e) {
      setError((e as Error).message);
    }
  };

  return (
    <div className="card space-y-3">
      <div className="flex flex-wrap items-center justify-between gap-2">
        <div className="flex items-center gap-2">
          <Dot on={node.online} />
          <span className="font-mono text-lg text-amber-300">{node.name}</span>
          <span className="text-xs text-slate-500">
            {node.online ? `heartbeat ${ago(node.lastHeartbeatAt)}` : `last seen ${ago(node.lastSeenAt)}`}
          </span>
        </div>
        <div className="flex flex-wrap gap-2">
          <button
            className="btn"
            onClick={() =>
              confirm(`Rotate the token of ${node.name}? The node disconnects until it has the new token.`) &&
              run(async () => onToken((await api.post<{ token: string }>(`/mgmt/admin/nodes/${node.id}/rotate`)).token))
            }
          >
            Rotate token
          </button>
          {node.hasToken && (
            <button
              className="btn-danger"
              onClick={() =>
                confirm(`Revoke the token of ${node.name}? The node disconnects immediately.`) &&
                run(() => api.post(`/mgmt/admin/nodes/${node.id}/revoke`))
              }
            >
              Revoke
            </button>
          )}
          <button
            className="btn-danger"
            onClick={() =>
              confirm(`Delete ${node.name} with all its publications and grants?`) && run(() => api.del(`/mgmt/admin/nodes/${node.id}`))
            }
          >
            Delete
          </button>
        </div>
      </div>
      <ErrorNote error={error} />
      <div className="grid gap-x-6 gap-y-1 text-sm sm:grid-cols-2 lg:grid-cols-4">
        <div>
          <span className="text-slate-500">Token </span>
          {node.hasToken ? <code className="font-mono text-xs">{node.tokenPrefix}_…</code> : <span className="text-red-400">revoked</span>}
        </div>
        <div>
          <span className="text-slate-500">Agent </span>
          {node.agentVersion ?? "–"}
        </div>
        <div>
          <span className="text-slate-500">Ollama </span>
          {node.ollamaVersion ?? "–"}
          {node.online && node.ollamaReachable === false && <span className="ml-1 text-red-400">(unreachable)</span>}
        </div>
        <div>
          <span className="text-slate-500">Load </span>
          {node.activeLeases} / {node.maxConcurrency}
        </div>
        {node.host && (
          <>
            <div>
              <span className="text-slate-500">CPU </span>
              {node.host.cpuCount} cores, load {node.host.loadAverage1?.toFixed(2) ?? "–"}
            </div>
            <div>
              <span className="text-slate-500">Memory </span>
              {bytes(node.host.memoryAvailableBytes)} free of {bytes(node.host.memoryTotalBytes)}
            </div>
          </>
        )}
        <div className="flex items-center gap-2">
          <span className="text-slate-500">Max concurrency</span>
          <input className="input w-16 py-0.5" value={max} onChange={(e) => setMax(e.target.value)} />
          {String(node.maxConcurrency) !== max && (
            <button className="btn py-0.5" onClick={() => run(() => api.patch(`/mgmt/admin/nodes/${node.id}`, { maxConcurrency: Number(max) }))}>
              Save
            </button>
          )}
        </div>
      </div>

      <div>
        <h3 className="h2 mt-2">Loaded models</h3>
        {node.loaded.length === 0 ? (
          <div className="text-sm text-slate-500">none</div>
        ) : (
          <table className="table">
            <thead>
              <tr>
                <th>Model</th>
                <th>Published as</th>
                <th>Memory</th>
                <th>VRAM</th>
                <th>Context</th>
                <th>Unloads in</th>
              </tr>
            </thead>
            <tbody>
              {node.loaded.map((m) => (
                <tr key={m.name}>
                  <td className="font-mono text-xs break-all">{m.name}</td>
                  <td className="font-mono text-amber-300">{m.aliases.join(", ") || "–"}</td>
                  <td>{bytes(m.size)}</td>
                  <td>{bytes(m.sizeVram)}</td>
                  <td>{m.contextLength ?? "–"}</td>
                  <td>{until(m.expiresAt)}</td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
      </div>

      <div>
        <h3 className="h2 mt-2">Active streams</h3>
        {node.streams.length === 0 ? (
          <div className="text-sm text-slate-500">idle</div>
        ) : (
          <table className="table">
            <thead>
              <tr>
                <th>User</th>
                <th>Model</th>
                <th>Endpoint</th>
                <th>Since</th>
              </tr>
            </thead>
            <tbody>
              {node.streams.map((s) => (
                <tr key={s.id}>
                  <td>{s.user}</td>
                  <td className="font-mono text-amber-300">{s.alias}</td>
                  <td className="font-mono text-xs">{s.endpoint}</td>
                  <td>{ago(s.since)}</td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
      </div>
    </div>
  );
}

export function NodesPage() {
  const { data, error, reload } = useResource<NodesResponse>("/mgmt/admin/nodes", 3000);
  const [name, setName] = useState("");
  const [max, setMax] = useState("2");
  const [token, setToken] = useState<string | null>(null);
  const [createError, setCreateError] = useState<string | null>(null);

  return (
    <div className="space-y-4">
      <h1 className="h1">Nodes</h1>
      <div className="card">
        <h2 className="h2">Add a node</h2>
        <form
          className="flex flex-wrap items-end gap-2"
          onSubmit={async (e) => {
            e.preventDefault();
            setCreateError(null);
            try {
              const res = await api.post<{ token: string }>("/mgmt/admin/nodes", { name, maxConcurrency: Number(max) });
              setToken(res.token);
              setName("");
              reload();
            } catch (err) {
              setCreateError((err as Error).message);
            }
          }}
        >
          <div>
            <label className="label">Name</label>
            <input className="input w-48" value={name} onChange={(e) => setName(e.target.value)} placeholder="diimitra" />
          </div>
          <div>
            <label className="label">Max concurrency</label>
            <input className="input w-24" value={max} onChange={(e) => setMax(e.target.value)} />
          </div>
          <button className="btn-primary" disabled={!name}>Create</button>
        </form>
        <ErrorNote error={createError} />
        {token && <TokenHelp token={token} />}
      </div>

      <ErrorNote error={error} />
      {data && (
        <div className="text-sm text-slate-400">
          Queue: <span className="text-slate-200">{data.queueDepth}</span> waiting
          {data.queued.length > 0 && <> ({data.queued.map((q) => `${q.user} → ${q.alias}`).join(", ")})</>}
        </div>
      )}
      {data?.nodes.length === 0 && <Empty>No nodes yet.</Empty>}
      {data?.nodes.map((n) => <NodeCard key={n.id} node={n} onChange={reload} onToken={setToken} />)}
    </div>
  );
}
