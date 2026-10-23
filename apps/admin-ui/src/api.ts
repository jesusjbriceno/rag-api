/**
 * The browser-side client for the same-origin admin BFF. The surface is exactly the
 * two fixed routes of the first-key workflow; the browser never selects a target,
 * never calls the admin API directly, and never receives or stores signing material.
 * Secrets returned by the BFF are returned to the caller only and must be kept in
 * volatile memory.
 */

export interface AdminClientMetadata {
  id: string;
  name: string;
  description: string | null;
  createdAt: string;
}

export interface AdminCredentialMetadata {
  id: string;
  clientId: string;
  keyId: string;
  description: string | null;
  version: number;
  state: string;
  createdAt: string;
  expiresAt: string | null;
}

export interface AdminCredentialDelivery {
  credential: AdminCredentialMetadata;
  secret: string;
}

/** A safe, user-facing failure: status-derived text only, never a raw response body. */
export class BffError extends Error {
  constructor(
    public readonly status: number,
    message: string,
  ) {
    super(message);
    this.name = "BffError";
  }
}

function messageFor(status: number, code: string): string {
  if (status === 0) {
    return "The admin service could not be reached. Check your connection and try again.";
  }
  switch (status) {
    case 400:
      return "The submission was rejected as invalid. Check the values and try again.";
    case 401:
      return "Your sign-in could not be verified. Reload the page and sign in again.";
    case 403:
      return "Your account is not authorized to use this console.";
    case 409:
      return code === "in_progress"
        ? "A previous submission with this key is still being processed. Wait a few seconds and retry."
        : "This submission conflicts with an earlier one. Wait a moment and try again.";
    case 413:
      return "The submission was too large.";
    default:
      if (status >= 500 || code === "upstream_unavailable") {
        return "The admin service is temporarily unavailable. Your progress is kept — retry in a moment.";
      }
      return "Something went wrong. If it keeps happening, contact the platform team.";
  }
}

async function readProblemCode(response: Response): Promise<string> {
  try {
    const payload: unknown = await response.json();
    if (payload && typeof payload === "object" && "code" in payload) {
      const code = (payload as { code: unknown }).code;
      if (typeof code === "string") {
        return code;
      }
    }
  } catch {
    // Redacted by design: an unreadable body yields a generic code, never its contents.
  }
  return "";
}

async function postJson<T>(path: string, body: unknown, idempotencyKey: string): Promise<T> {
  let response: Response;
  try {
    response = await fetch(path, {
      method: "POST",
      headers: {
        "Content-Type": "application/json",
        "Idempotency-Key": idempotencyKey,
      },
      body: JSON.stringify(body),
    });
  } catch {
    throw new BffError(0, messageFor(0, ""));
  }

  if (!response.ok) {
    throw new BffError(response.status, messageFor(response.status, await readProblemCode(response)));
  }

  const payload: unknown = await response.json();
  if (payload === null || typeof payload !== "object") {
    throw new BffError(response.status, messageFor(500, ""));
  }
  return payload as T;
}

/** Creates a client through the fixed BFF create route. */
export function createClient(
  name: string,
  description: string | null,
  idempotencyKey: string,
): Promise<AdminClientMetadata> {
  return postJson<AdminClientMetadata>("/api/admin/clients", { name, description }, idempotencyKey);
}

/** Issues the first credential for a client through the fixed BFF issue route. */
export function issueFirstCredential(
  clientId: string,
  description: string | null,
  idempotencyKey: string,
): Promise<AdminCredentialDelivery> {
  return postJson<AdminCredentialDelivery>(
    `/api/admin/clients/${encodeURIComponent(clientId)}/credentials`,
    { description },
    idempotencyKey,
  );
}

/** One UUID idempotency key per logical mutation. */
export function newIdempotencyKey(): string {
  if (typeof crypto.randomUUID === "function") {
    return crypto.randomUUID();
  }
  const bytes = crypto.getRandomValues(new Uint8Array(16));
  bytes[6] = (bytes[6] & 0x0f) | 0x40;
  bytes[8] = (bytes[8] & 0x3f) | 0x80;
  const hex = Array.from(bytes, (byte) => byte.toString(16).padStart(2, "0")).join("");
  return `${hex.slice(0, 8)}-${hex.slice(8, 12)}-${hex.slice(12, 16)}-${hex.slice(16, 20)}-${hex.slice(20)}`;
}
