import { type MyModel } from "../api";
import { CopyButton, Dot, Empty, ErrorNote } from "../components/Bits";
import { useResource } from "../hooks";

export function ConnectBox({ example }: { example?: string }) {
  const { data } = useResource<{ publicBaseUrl: string }>("/mgmt/info");
  const base = data?.publicBaseUrl ?? window.location.origin;
  const model = example ?? "MODEL";
  const curl = `curl ${base}/api/chat \\
  -H "Authorization: Bearer $OLLAMA_RELAY_KEY" \\
  -d '{"model":"${model}","messages":[{"role":"user","content":"Hello!"}]}'`;
  return (
    <div className="card">
      <h2 className="h2">How to connect</h2>
      <div className="grid gap-2 text-sm md:grid-cols-[10rem_1fr]">
        <div className="text-slate-400">Ollama API</div>
        <div className="flex items-center gap-2">
          <code className="font-mono text-amber-300">{base}</code>
          <CopyButton text={base} />
        </div>
        <div className="text-slate-400">OpenAI compatible</div>
        <div className="flex items-center gap-2">
          <code className="font-mono text-amber-300">{base}/v1</code>
          <CopyButton text={`${base}/v1`} />
        </div>
        <div className="text-slate-400">Authentication</div>
        <div>
          <code className="font-mono">Authorization: Bearer orl_…</code> with a key from <a className="text-amber-400 hover:underline" href="/keys">API keys</a>.
        </div>
      </div>
      <pre className="mt-3 overflow-x-auto rounded bg-slate-950 p-3 font-mono text-xs text-slate-300">{curl}</pre>
      <p className="mt-2 text-xs text-slate-500">
        Open WebUI: add an Ollama connection with the first URL and your key, or an OpenAI connection with the /v1 URL.
      </p>
    </div>
  );
}

export function MyModelsPage() {
  const { data, error } = useResource<MyModel[]>("/mgmt/me/models", 5000);
  return (
    <div className="space-y-4">
      <h1 className="h1">My models</h1>
      <ErrorNote error={error} />
      <div className="card">
        {data && data.length === 0 && <Empty>No models are granted to you yet. Ask the admin.</Empty>}
        {data && data.length > 0 && (
          <table className="table">
            <thead>
              <tr>
                <th>Model</th>
                <th>Status</th>
              </tr>
            </thead>
            <tbody>
              {data.map((m) => (
                <tr key={m.alias}>
                  <td className="font-mono text-amber-300">{m.alias}</td>
                  <td>
                    <span className="inline-flex items-center gap-2">
                      <Dot on={m.online} />
                      {m.online ? (m.loaded ? "online, loaded" : "online") : "offline"}
                    </span>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
      </div>
      <ConnectBox example={data?.[0]?.alias} />
    </div>
  );
}
