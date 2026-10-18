import { getAccessToken, signIn } from '../auth/auth';
import { client } from './gen/client.gen';

// The SPA always talks to its own origin: Vite proxies /api in dev, nginx proxies it in prod.
client.setConfig({ baseUrl: '', auth: () => getAccessToken(), headers: { 'X-Kupro-Client': 'web' } });

// An expired or revoked session sends the user back through Keycloak.
client.interceptors.response.use((response) => {
  if (response.status === 401) void signIn();
  return response;
});

export { client };
