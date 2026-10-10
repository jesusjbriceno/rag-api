import { useRef, useState, type FormEvent } from "react";
import {
  BffError,
  createClient,
  issueFirstCredential,
  newIdempotencyKey,
  type AdminClientMetadata,
  type AdminCredentialMetadata,
  type AdminCredentialDelivery,
} from "./api";

interface IssueAttempt {
  idempotencyKey: string;
  description: string | null;
}

type FlowState =
  | { phase: "form"; submitting: boolean; error: string | null }
  | { phase: "issuing"; client: AdminClientMetadata; attempt: IssueAttempt; submitting: boolean; error: string | null }
  | { phase: "secret"; client: AdminClientMetadata; delivery: AdminCredentialDelivery; confirmed: boolean }
  | { phase: "done"; client: AdminClientMetadata; credential: AdminCredentialMetadata };

const MAX_NAME_LENGTH = 200;

/**
 * One-time client/key issuance console. All state is volatile React state: the issued
 * secret never touches persistent browser storage, the URL, or the console, is shown
 * once, can be copied only on an explicit click, and cannot be dismissed until the
 * administrator confirms it was saved. Each logical browser mutation carries one UUID
 * Idempotency-Key that is reused unchanged across retries.
 */
export default function App() {
  const [flow, setFlow] = useState<FlowState>({ phase: "form", submitting: false, error: null });
  const [name, setName] = useState("");
  const [description, setDescription] = useState("");

  // Per-logical-mutation idempotency keys, kept in refs: reused unchanged on retries,
  // regenerated only when the mutation itself changes or a brand-new flow starts.
  const createAttemptRef = useRef<{ idempotencyKey: string; name: string; description: string } | null>(null);

  async function runIssuance(client: AdminClientMetadata, attempt: IssueAttempt): Promise<void> {
    setFlow({ phase: "issuing", client, attempt, submitting: true, error: null });
    try {
      const delivery = await issueFirstCredential(client.id, attempt.description, attempt.idempotencyKey);
      setFlow({ phase: "secret", client, delivery, confirmed: false });
    } catch (error) {
      const message =
        error instanceof BffError ? error.message : "The admin service is temporarily unavailable. Your progress is kept — retry in a moment.";
      setFlow({ phase: "issuing", client, attempt, submitting: false, error: message });
    }
  }

  async function handleSubmit(event: FormEvent): Promise<void> {
    event.preventDefault();
    if (flow.phase !== "form" || flow.submitting) {
      return;
    }

    const trimmedName = name.trim();
    if (trimmedName.length === 0 || trimmedName.length > MAX_NAME_LENGTH) {
      setFlow({ phase: "form", submitting: false, error: "A client name of 1 to 200 characters is required." });
      return;
    }

    // One UUID key per logical client-create mutation: an unchanged retry reuses the
    // exact key, while edited content is a new mutation and gets a fresh key.
    const previous = createAttemptRef.current;
    const idempotencyKey =
      previous && previous.name === trimmedName && previous.description === description
        ? previous.idempotencyKey
        : newIdempotencyKey();
    createAttemptRef.current = { idempotencyKey, name: trimmedName, description };

    const trimmedDescription = description.trim();
    setFlow({ phase: "form", submitting: true, error: null });
    try {
      const client = await createClient(trimmedName, trimmedDescription === "" ? null : trimmedDescription, idempotencyKey);
      // Client creation succeeded: the logical mutation continues into credential
      // issuance with its own distinct key, reusable if issuance needs a retry.
      await runIssuance(client, { idempotencyKey: newIdempotencyKey(), description: null });
    } catch (error) {
      const message =
        error instanceof BffError ? error.message : "The admin service is temporarily unavailable. Your progress is kept — retry in a moment.";
      setFlow({ phase: "form", submitting: false, error: message });
    }
  }

  function resetForNewClient(): void {
    createAttemptRef.current = null;
    setName("");
    setDescription("");
    setFlow({ phase: "form", submitting: false, error: null });
  }

  return (
    <main>
      <h1>Client onboarding</h1>

      {flow.phase === "form" && (
        <form onSubmit={handleSubmit} aria-label="Create client">
          <label htmlFor="client-name">Client name</label>
          <input
            id="client-name"
            name="client-name"
            type="text"
            maxLength={MAX_NAME_LENGTH}
            required
            value={name}
            onChange={(event) => setName(event.target.value)}
          />
          <label htmlFor="client-description">Client description (optional)</label>
          <input
            id="client-description"
            name="client-description"
            type="text"
            maxLength={MAX_NAME_LENGTH}
            value={description}
            onChange={(event) => setDescription(event.target.value)}
          />
          {flow.error && <p role="alert">{flow.error}</p>}
          <button type="submit" disabled={flow.submitting}>
            {flow.submitting ? "Creating client…" : "Create client and issue credential"}
          </button>
        </form>
      )}

      {flow.phase === "issuing" && (
        <section aria-label="Issue credential">
          <p>Client “{flow.client.name}” was created. Issuing its first credential…</p>
          {flow.error && (
            <>
              <p role="alert">{flow.error}</p>
              <p>
                The client already exists, so you can retry the credential issuance without
                creating a duplicate client.
              </p>
              <button
                type="button"
                onClick={() => {
                  if (!flow.submitting) {
                    void runIssuance(flow.client, flow.attempt);
                  }
                }}
                disabled={flow.submitting}
              >
                Retry credential issuance
              </button>
            </>
          )}
        </section>
      )}

      {flow.phase === "secret" && (
        <section aria-label="One-time credential secret" className="issued-secret">
          <h2>Save the secret now</h2>
          <p>
            This is the only time the secret for credential <code>{flow.delivery.credential.keyId}</code>{" "}
            of client “{flow.client.name}” is shown. Store it in a safe place; it cannot be
            retrieved again.
          </p>
          <output data-testid="issued-secret" aria-label="Issued credential secret">
            {flow.delivery.secret}
          </output>
          <div>
            <button type="button" onClick={() => void navigator.clipboard?.writeText(flow.delivery.secret)}>
              Copy secret
            </button>
          </div>
          <label>
            <input
              type="checkbox"
              checked={flow.confirmed}
              onChange={(event) =>
                setFlow({ ...flow, confirmed: (event.target as HTMLInputElement).checked })
              }
            />{" "}
            I saved this secret in a safe place
          </label>
          <button
            type="button"
            disabled={!flow.confirmed}
            onClick={() =>
              setFlow({
                phase: "done",
                client: flow.client,
                credential: flow.delivery.credential,
              })
            }
          >
            Dismiss secret
          </button>
        </section>
      )}

      {flow.phase === "done" && (
        <section role="status" aria-label="Onboarding complete">
          <p>
            Client “{flow.client.name}” was onboarded. Credential{" "}
            <code>{flow.credential.keyId}</code> is active.
          </p>
          <button type="button" onClick={resetForNewClient}>
            Onboard another client
          </button>
        </section>
      )}
    </main>
  );
}
