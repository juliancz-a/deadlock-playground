import type { APIRoute } from 'astro';
import changelogData from '../../data/changelog.json';

export const GET: APIRoute = async () => {
  return new Response(JSON.stringify(changelogData, null, 2), {
    status: 200,
    headers: {
      'Content-Type': 'application/json',
      'Access-Control-Allow-Origin': '*',
      'Cache-Control': 'public, max-age=300'
    }
  });
};
