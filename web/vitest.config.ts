import react from '@vitejs/plugin-react'
import { defineConfig } from 'vitest/config'

// DOM tests (tests/dom/*.test.tsx) for components and DOM helpers, on jsdom. The rest of the tests are
// plain node tests (npm run test:unit). Nothing here reaches a server: the API and the document renderers
// are mocked in each test.
export default defineConfig({
  plugins: [react()],
  test: {
    environment: 'jsdom',
    include: ['tests/dom/**/*.test.tsx'],
    restoreMocks: true,
  },
})
