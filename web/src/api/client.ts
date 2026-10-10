import { client } from './gen/client.gen';

// The SPA always talks to its own origin: Vite proxies /api in dev, nginx proxies it in prod.
client.setConfig({ baseUrl: '' });

export { client };
