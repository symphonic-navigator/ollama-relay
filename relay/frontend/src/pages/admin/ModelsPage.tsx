import { useEffect, useState } from "react";
import { api, type NodesResponse } from "../../api";
import { Empty, ErrorNote } from "../../components/Bits";
import { bytes } from "../../format";
import { useResource } from "../../hooks";

type Reported = {
  models: {
    name: string;
    size: number;
    family: string | null;
    parameterSize: string | null;
    quantizationLevel: string | null;
    publications: { id: string; alias: string }[];
  }[];
  orphaned: { id: string; alias: string; upstreamModel: string }[];
};

function suggestAlias(name: string) {
  const base = name.split("/").pop() ?? name;
  return base
    .toLowerCase()
    .replace(/:latest$/, "")
    .replace(/[^a-z0-9._-]+/g, "-")
    .replace(/^[^a-z0-9]+/, "")
    .slice(0, 63);
}

function PublishRow({ nodeId, name, onDone }: { nodeId: string; name: string; onDone: () => void }) {
  const [alias, setAlias] = useState(suggestAlias(name));
  const [error, setError] = useState<string | null>(null);
  return (
    <div className="flex flex-wrap items-center gap-2">
      <input className="input w-56 py-0.5 font-mono" value={alias} onChange={(e) => setAlias(e.target.value)} />
      <button
        className="btn-primary py-0.5"
        onClick={async () => {
          setError(null);
          try {
            await api.post("/mgmt/admin/publications", { nodeId, upstreamModel: name, alias });
            onDone();
          } catch (e) {
            setError((e as Error).message);
          }
        }}
      >
        Publish
      </button>
      {error && <span className="text-xs text-red-400">{error}</span>}
    </div>
  );
}

function PublishedAlias({ id, alias, onDone }: { id: string; alias: string; onDone: () => void }) {
  const [editing, setEditing] = useState(false);
  const [value, setValue] = useState(alias);
  const [error, setError] = useState<string | null>(null);
  if (editing)
    return (
      <span className="inline-flex flex-wrap items-center gap-1">
        <input className="input w-44 py-0.5 font-mono" value={value} onChange={(e) => setValue(e.target.value)} />
        <button
          className="btn py-0.5"
          onClick={async () => {
            try {
              await api.patch(`/mgmt/admin/publications/${id}`, { alias: value });
              setEditing(false);
              onDone();
            } catch (e) {
              setError((e as Error).message);
            }
          }}
        >
          Save
        </button>
        <button className="btn py-0.5" onClick={() => setEditing(false)}>Cancel</button>
        {error && <span className="text-xs text-red-400">{error}</span>}
      </span>
    );
  return (
    <span className="inline-flex items-center gap-1 rounded border border-amber-800 bg-amber-950/40 px-2 py-0.5 font-mono text-xs text-amber-300">
      {alias}
      <button className="text-slate-400 hover:text-slate-200" title="Rename" onClick={() => setEditing(true)}>✎</button>
      <button
        className="text-slate-400 hover:text-red-400"
        title="Unpublish"
        onClick={async () => {
          if (!confirm(`Unpublish ${alias}? Its grants are removed.`)) return;
          await api.del(`/mgmt/admin/publications/${id}`);
          onDone();
        }}
      >
        ✕
      </button>
    </span>
  );
}

export function ModelsPage() {
  const nodes = useResource<NodesResponse>("/mgmt/admin/nodes");
  const [nodeId, setNodeId] = useState<string | null>(null);
  useEffect(() => {
    if (!nodeId && nodes.data?.nodes[0]) setNodeId(nodes.data.nodes[0].id);
  }, [nodes.data, nodeId]);
  const reported = useResource<Reported>(nodeId ? `/mgmt/admin/nodes/${nodeId}/models` : null);
  const [filter, setFilter] = useState("");

  return (
    <div className="space-y-4">
      <h1 className="h1">Publish models</h1>
      <p className="text-sm text-slate-400">
        Nodes report every model Ollama has locally. Nothing is usable until it is published under an alias and granted to users.
        The same alias on several nodes forms a pool.
      </p>
      <ErrorNote error={nodes.error ?? reported.error} />
      <div className="flex flex-wrap gap-2">
        {nodes.data?.nodes.map((n) => (
          <button key={n.id} className={n.id === nodeId ? "btn-primary" : "btn"} onClick={() => setNodeId(n.id)}>
            {n.name}
          </button>
        ))}
        <input className="input ml-auto w-56" placeholder="Filter…" value={filter} onChange={(e) => setFilter(e.target.value)} />
      </div>
      <div className="card">
        {reported.data && reported.data.models.length === 0 && <Empty>This node has not reported any models yet.</Empty>}
        {reported.data && reported.data.models.length > 0 && (
          <table className="table">
            <thead>
              <tr>
                <th>Model on node</th>
                <th>Size</th>
                <th>Details</th>
                <th>Published as</th>
              </tr>
            </thead>
            <tbody>
              {reported.data.models
                .filter((m) => m.name.toLowerCase().includes(filter.toLowerCase()))
                .map((m) => (
                  <tr key={m.name}>
                    <td className="font-mono text-xs break-all">{m.name}</td>
                    <td className="whitespace-nowrap">{bytes(m.size)}</td>
                    <td className="text-xs text-slate-400">
                      {[m.family, m.parameterSize, m.quantizationLevel].filter(Boolean).join(" · ")}
                    </td>
                    <td className="space-y-1">
                      <div className="flex flex-wrap gap-1">
                        {m.publications.map((p) => (
                          <PublishedAlias key={p.id} id={p.id} alias={p.alias} onDone={reported.reload} />
                        ))}
                      </div>
                      {m.publications.length === 0 && <PublishRow nodeId={nodeId!} name={m.name} onDone={reported.reload} />}
                    </td>
                  </tr>
                ))}
            </tbody>
          </table>
        )}
        {reported.data && reported.data.orphaned.length > 0 && (
          <div className="mt-4">
            <h2 className="h2">Published but no longer on the node</h2>
            <div className="flex flex-wrap gap-2">
              {reported.data.orphaned.map((o) => (
                <span key={o.id} className="text-xs text-slate-400">
                  <PublishedAlias id={o.id} alias={o.alias} onDone={reported.reload} /> → {o.upstreamModel}
                </span>
              ))}
            </div>
          </div>
        )}
      </div>
    </div>
  );
}
