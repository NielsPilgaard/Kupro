import { UserManager, type User } from 'oidc-client-ts';
import i18n from '../i18n';

// Keycloak OIDC, authorization code + PKCE (ADR-005). VITE_KEYCLOAK_URL is set by Aspire in dev and baked into the
// image at build time in prod (ADR-012 IMP-005).
const keycloakUrl = (import.meta.env.VITE_KEYCLOAK_URL as string | undefined) ?? 'https://localhost:8180';

export const callbackPath = '/auth/callback';

export const userManager = new UserManager({
  authority: `${keycloakUrl.replace(/\/$/, '')}/realms/kupro`,
  client_id: 'web',
  redirect_uri: `${window.location.origin}${callbackPath}`,
  post_logout_redirect_uri: window.location.origin,
  response_type: 'code',
  scope: 'openid profile email',
  automaticSilentRenew: true,
});

/** Current session, or null when signed out or the access token has expired. */
export async function getSession(): Promise<User | null> {
  const user = await userManager.getUser();
  return user && !user.expired ? user : null;
}

export async function getAccessToken(): Promise<string | undefined> {
  return (await getSession())?.access_token;
}

/** Sends the browser to Keycloak; resolves never, since the page unloads. */
export async function signIn(returnTo: string = currentPath()): Promise<never> {
  // Keycloak's pages show no language picker; they follow the app's language.
  await userManager.signinRedirect({ state: { returnTo }, ui_locales: i18n.language });
  return new Promise<never>(() => {});
}

export async function signOut(): Promise<void> {
  await userManager.signoutRedirect();
}

/** Completes the redirect back from Keycloak and returns where the user was going. */
export async function completeSignIn(): Promise<string> {
  const user = await userManager.signinRedirectCallback();
  const state = user.state as { returnTo?: string } | undefined;
  return state?.returnTo?.startsWith('/') ? state.returnTo : '/';
}

function currentPath() {
  return window.location.pathname + window.location.search;
}
