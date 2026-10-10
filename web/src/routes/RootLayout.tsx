import { Outlet } from '@tanstack/react-router';

export function RootLayout() {
  return (
    <div className="h-full bg-bg text-ink">
      <Outlet />
    </div>
  );
}
