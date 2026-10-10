import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, describe, expect, it, vi } from "vitest";
import App from "./App";

const UUID_PATTERN = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

const CLIENT_ID = "d1c0ffee-0000-0000-0000-000000000001";
const CREDENTIAL_ID = "feedface-0000-0000-0000-000000000002";
const SECRET = "rag_sk_TEST-SECRET-VALUE-0001";

const CLIENT_PAYLOAD = {
  id: CLIENT_ID,
  name: "Acme",
  description: null,
  createdAt: "2025-01-01T00:00:00Z",
};

const DELIVERY_PAYLOAD = {
  credential: {
    id: CREDENTIAL_ID,
    clientId: CLIENT_ID,
    keyId: "ck_test_0001",
    description: null,
    version: 1,
    state: "active",
    createdAt: "2025-01-01T00:00:00Z",
    expiresAt: null,
  },
  secret: SECRET,
};

interface RecordedRequest {
  url: string;
  method: string;
  headers: Record<string, string>;
  body: string;
}

/**
 * Installs a scripted fetch mock. The browser must call only the two fixed
 * same-origin BFF routes, so every observed call is recorded for assertions.
 */
function scriptFetch(handler: (request: RecordedRequest) => Response): RecordedRequest[] {
  const calls: RecordedRequest[] = [];
  const fetchMock = vi.fn(async (input: RequestInfo | URL, init?: RequestInit): Promise<Response> => {
    const url =
      typeof input === "string" ? input : input instanceof URL ? input.toString() : input.url;
    const headers: Record<string, string> = {};
    const raw = init?.headers;
    if (raw && typeof raw === "object" && !(raw instanceof Headers)) {
      for (const [name, value] of Object.entries(raw as Record<string, string>)) {
        headers[name] = value;
      }
    }
    const request: RecordedRequest = {
      url,
      method: init?.method ?? "GET",
      headers,
      body: typeof init?.body === "string" ? init.body : "",
    };
    calls.push(request);
    return handler(request);
  });
  vi.stubGlobal("fetch", fetchMock);
  return calls;
}

function jsonResponse(status: number, payload: unknown): Response {
  return new Response(JSON.stringify(payload), {
    status,
    headers: { "Content-Type": "application/json" },
  });
}

function problemResponse(status: number, code: string): Response {
  return jsonResponse(status, { status, title: "Problem", code });
}

async function submitClientForm(user: ReturnType<typeof userEvent.setup>): Promise<void> {
  await user.type(screen.getByLabelText("Client name"), "Acme");
  await user.click(screen.getByRole("button", { name: "Create client and issue credential" }));
}

afterEach(() => {
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

describe("first-credential onboarding flow", () => {
  it("creates a client and issues the first credential through the two fixed BFF routes only", async () => {
    const user = userEvent.setup();
    const consoleSpies = ["log", "info", "warn", "error"].map((level) =>
      vi.spyOn(console, level as "log").mockImplementation(() => {}),
    );
    const calls = scriptFetch((request) => {
      if (request.url === "/api/admin/clients" && request.method === "POST") {
        return jsonResponse(201, CLIENT_PAYLOAD);
      }
      if (request.url === `/api/admin/clients/${CLIENT_ID}/credentials` && request.method === "POST") {
        return jsonResponse(201, DELIVERY_PAYLOAD);
      }
      return jsonResponse(404, { code: "unexpected_route" });
    });

    render(<App />);
    await submitClientForm(user);

    expect(await screen.findByTestId("issued-secret")).toHaveTextContent(SECRET);

    // The browser surface is exactly the two fixed BFF routes: never an admin API URL,
    // never an arbitrary path.
    expect(calls.map((call) => call.url)).toEqual([
      "/api/admin/clients",
      `/api/admin/clients/${CLIENT_ID}/credentials`,
    ]);

    // One distinct UUID idempotency key per logical mutation.
    const createKey = calls[0].headers["Idempotency-Key"];
    const issueKey = calls[1].headers["Idempotency-Key"];
    expect(createKey).toMatch(UUID_PATTERN);
    expect(issueKey).toMatch(UUID_PATTERN);
    expect(issueKey).not.toEqual(createKey);

    // The secret lives only in volatile memory: never persisted, never logged.
    expect(window.localStorage.getItem(window.localStorage.key(0) ?? "")).toBeNull();
    expect(window.sessionStorage.getItem(window.sessionStorage.key(0) ?? "")).toBeNull();
    for (const spy of consoleSpies) {
      for (const argument of spy.mock.calls.flat()) {
        expect(String(argument)).not.toContain(SECRET);
      }
    }
  });

  it("keeps a recoverable state and reuses the issuance idempotency key when credential issuance fails", async () => {
    const user = userEvent.setup();
    let issuanceAttempts = 0;
    const calls = scriptFetch((request) => {
      if (request.url === "/api/admin/clients" && request.method === "POST") {
        return jsonResponse(201, CLIENT_PAYLOAD);
      }
      issuanceAttempts += 1;
      return issuanceAttempts === 1
        ? problemResponse(502, "upstream_unavailable")
        : jsonResponse(201, DELIVERY_PAYLOAD);
    });

    render(<App />);
    await submitClientForm(user);

    // Client creation succeeded, so the flow must not restart from an empty form:
    // a recoverable state explains what happened and offers a retry.
    expect(await screen.findByRole("alert")).toHaveTextContent(/temporarily unavailable/i);
    expect(screen.queryByTestId("issued-secret")).not.toBeInTheDocument();

    await user.click(screen.getByRole("button", { name: "Retry credential issuance" }));

    expect(await screen.findByTestId("issued-secret")).toHaveTextContent(SECRET);

    // Both issuance attempts are one logical mutation: the exact same UUID key,
    // which differs from the client-create key.
    const issuanceCalls = calls.filter((call) => call.url.endsWith("/credentials"));
    expect(issuanceCalls).toHaveLength(2);
    expect(issuanceCalls[0].headers["Idempotency-Key"]).toMatch(UUID_PATTERN);
    expect(issuanceCalls[1].headers["Idempotency-Key"]).toEqual(
      issuanceCalls[0].headers["Idempotency-Key"],
    );
    const createKey = calls.find((call) => call.url === "/api/admin/clients")?.headers["Idempotency-Key"];
    expect(issuanceCalls[0].headers["Idempotency-Key"]).not.toEqual(createKey);
  });

  it("reuses the client-create idempotency key for unchanged retries and issues a fresh one when the mutation changes", async () => {
    const user = userEvent.setup();
    let createAttempts = 0;
    const calls = scriptFetch((request) => {
      if (request.url === "/api/admin/clients" && request.method === "POST") {
        createAttempts += 1;
        return createAttempts <= 2
          ? problemResponse(502, "upstream_unavailable")
          : jsonResponse(201, { ...CLIENT_PAYLOAD, name: request.body.includes("Renamed") ? "Renamed" : "Acme" });
      }
      return jsonResponse(201, DELIVERY_PAYLOAD);
    });

    render(<App />);
    await submitClientForm(user);

    expect(await screen.findByRole("alert")).toHaveTextContent(/temporarily unavailable/i);

    // Unchanged retry: same logical mutation.
    await user.click(screen.getByRole("button", { name: "Create client and issue credential" }));
    expect(await screen.findByRole("alert")).toHaveTextContent(/temporarily unavailable/i);

    // Editing the name is a different logical mutation: a new key is generated.
    const nameInput = screen.getByLabelText("Client name");
    await user.clear(nameInput);
    await user.type(nameInput, "Renamed");
    await user.click(screen.getByRole("button", { name: "Create client and issue credential" }));

    expect(await screen.findByTestId("issued-secret")).toHaveTextContent(SECRET);

    // The unchanged retries share one UUID key and body; the edited mutation gets
    // a new key and body; the credential issuance key differs from all of them.
    const createCalls = calls.filter((call) => call.url === "/api/admin/clients");
    expect(createCalls).toHaveLength(3);
    expect(createCalls[0].body).toEqual(createCalls[1].body);
    expect(createCalls[0].headers["Idempotency-Key"]).toEqual(createCalls[1].headers["Idempotency-Key"]);
    expect(createCalls[0].headers["Idempotency-Key"]).toMatch(UUID_PATTERN);
    expect(createCalls[2].body).not.toEqual(createCalls[0].body);
    expect(createCalls[2].headers["Idempotency-Key"]).toMatch(UUID_PATTERN);
    expect(createCalls[2].headers["Idempotency-Key"]).not.toEqual(createCalls[0].headers["Idempotency-Key"]);

    const issueKey = calls.find((call) => call.url.endsWith("/credentials"))?.headers["Idempotency-Key"];
    expect(issueKey).toMatch(UUID_PATTERN);
    for (const createCall of createCalls) {
      expect(issueKey).not.toEqual(createCall.headers["Idempotency-Key"]);
    }
  });

  it("gates secret dismissal behind an explicit save confirmation and clears the secret afterwards", async () => {
    const user = userEvent.setup();
    const clipboard = vi.fn();
    Object.defineProperty(window.navigator, "clipboard", {
      value: { writeText: clipboard },
      configurable: true,
    });
    scriptFetch((request) => {
      if (request.url === "/api/admin/clients" && request.method === "POST") {
        return jsonResponse(201, CLIENT_PAYLOAD);
      }
      return jsonResponse(201, DELIVERY_PAYLOAD);
    });

    render(<App />);
    await submitClientForm(user);
    expect(await screen.findByTestId("issued-secret")).toHaveTextContent(SECRET);

    // Copying requires an explicit click and sends exactly the secret.
    await user.click(screen.getByRole("button", { name: "Copy secret" }));
    expect(clipboard).toHaveBeenCalledWith(SECRET);

    // Dismissal is blocked until the administrator confirms the secret was saved.
    const dismiss = screen.getByRole("button", { name: "Dismiss secret" });
    expect(dismiss).toBeDisabled();
    await user.click(dismiss);
    expect(screen.getByTestId("issued-secret")).toBeInTheDocument();

    await user.click(screen.getByLabelText("I saved this secret in a safe place"));
    expect(dismiss).toBeEnabled();
    await user.click(dismiss);

    // One-time display: after dismissal the secret is unrecoverable in the UI.
    await waitFor(() => {
      expect(screen.queryByTestId("issued-secret")).not.toBeInTheDocument();
      expect(screen.queryByText(SECRET)).not.toBeInTheDocument();
    });
    expect(window.localStorage.getItem(window.localStorage.key(0) ?? "")).toBeNull();
    expect(window.sessionStorage.getItem(window.sessionStorage.key(0) ?? "")).toBeNull();
    expect(screen.getByRole("status")).toBeInTheDocument();
  });
});
