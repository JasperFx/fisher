import { defineConfig, type DefaultTheme, type UserConfig } from "vitepress"
import llmstxt from 'vitepress-plugin-llms'

const config: UserConfig<DefaultTheme.Config> = {
  base: '/',
  lang: 'en-US',
  title: 'Fisher',
  description: '.NET Event Store and Document Database on SQLite',
  head: [
    ['link', { rel: 'icon', href: '/logo.png' }],
    ['meta', { property: 'og:title', content: 'Fisher' }],
    ['meta', { property: 'og:type', content: 'website' }],
    ['meta', { property: 'og:image', content: '/banner.png' }],
    ['meta', { property: 'og:description', content: '.NET Event Store and Document Database on SQLite' }],
  ],

  lastUpdated: true,

  themeConfig: {
    logo: '/logo.png',

    nav: [
      { text: 'Why Fisher?', link: '/whitepaper' },
      { text: 'Intro', link: '/introduction' },
      { text: 'Document DB', link: '/documents/', activeMatch: '/documents/' },
      { text: 'Event Store', link: '/events/', activeMatch: '/events/' },
      { text: 'Support Plans', link: 'https://www.jasperfx.net/support-plans/' },
    ],

    search: {
      provider: 'local'
    },

    editLink: {
      pattern: 'https://github.com/JasperFX/fisher/edit/main/docs/:path',
      text: 'Suggest changes to this page'
    },

    socialLinks: [
      { icon: 'github', link: 'https://github.com/JasperFX/fisher' },
    ],

    footer: {
      message: 'Released under the MIT License.',
      copyright: 'Copyright © Jeremy D. Miller and contributors.',
    },

    sidebar: {
      '/': [
        {
          text: 'Introduction',
          collapsed: true,
          items: [
            { text: 'Why Fisher?', link: '/whitepaper' },
            { text: 'What is Fisher?', link: '/introduction' },
            { text: 'Getting Started', link: '/getting-started' },
          ]
        },
        {
          text: 'Configuration',
          collapsed: true,
          items: [
            { text: 'Bootstrapping Fisher', link: '/configuration/hostbuilder' },
            { text: 'Configuring Document Storage', link: '/configuration/storeoptions' },
            { text: 'SQLite and PRAGMA Settings', link: '/configuration/sqlite' },
            { text: 'JSON Serialization', link: '/configuration/json' },
            { text: 'Resiliency Policies', link: '/configuration/retries' },
            { text: 'Multi-Tenancy', link: '/configuration/multitenancy' },
            { text: 'Multiple Stores', link: '/configuration/multiple-stores' },
          ]
        },
        {
          text: 'Document Database',
          collapsed: true,
          items: [
            { text: 'Fisher as Document DB', link: '/documents/' },
            { text: 'Document Identity', link: '/documents/identity' },
            { text: 'Database Storage', link: '/documents/storage' },
            { text: 'Fisher Metadata', link: '/documents/metadata' },
            { text: 'Opening Sessions', link: '/documents/sessions' },
            { text: 'Session Listeners', link: '/documents/listeners' },
            { text: 'Storing Documents', link: '/documents/storing' },
            { text: 'Deleting Documents', link: '/documents/deletes' },
            { text: 'Document Hierarchies', link: '/documents/hierarchies' },
            {
              text: 'Indexing Documents', link: '/documents/indexing/', collapsed: true, items: [
                { text: 'Duplicated Fields', link: '/documents/indexing/duplicated-fields' },
                { text: 'Declared Indexes', link: '/documents/indexing/indexes' },
                { text: 'Foreign Keys', link: '/documents/indexing/foreign-keys' },
              ]
            },
            {
              text: 'Querying Documents', link: '/documents/querying/', collapsed: true, items: [
                { text: 'Loading Documents by Id', link: '/documents/querying/byid' },
                { text: 'Querying Documents with LINQ', link: '/documents/querying/linq/' },
                { text: 'Supported LINQ Operators', link: '/documents/querying/linq/operators' },
                { text: 'Searching on String Fields', link: '/documents/querying/linq/strings' },
                { text: 'Paging', link: '/documents/querying/linq/paging' },
                { text: 'Grouping and Aggregates', link: '/documents/querying/linq/grouping' },
                { text: 'Joins', link: '/documents/querying/linq/joins' },
                { text: 'Full-Text Search', link: '/documents/querying/linq/full-text' },
                { text: 'Including Related Documents', link: '/documents/querying/linq/includes' },
                { text: 'Querying for Raw JSON', link: '/documents/querying/query-json' },
                { text: 'Batched Queries', link: '/documents/querying/batched-queries' },
                { text: 'Raw SQL', link: '/documents/querying/raw-sql' },
              ]
            },
            { text: 'Multi-Tenanted Documents', link: '/documents/multi-tenancy' },
            { text: 'Initial Baseline Data', link: '/documents/initial-data' },
            { text: 'Optimistic Concurrency', link: '/documents/concurrency' },
            { text: 'Partial Updates/Patching', link: '/documents/partial-updates-patching' },
            { text: 'Bulk Insert', link: '/documents/bulk-insert' },
            { text: 'Transaction Participants', link: '/documents/transaction-participants' },
            { text: 'ASP.NET Core Integration', link: '/documents/aspnetcore' },
          ]
        },
        {
          text: 'Event Store',
          collapsed: true,
          items: [
            { text: 'Fisher as Event Store', link: '/events/' },
            { text: 'Quick Start', link: '/events/quickstart' },
            { text: 'Storage', link: '/events/storage' },
            { text: 'Appending Events', link: '/events/appending' },
            { text: 'Querying Events', link: '/events/querying' },
            { text: 'Metadata', link: '/events/metadata' },
            { text: 'Archiving Streams', link: '/events/archiving' },
            { text: 'Snapshots', link: '/events/snapshots' },
            { text: 'Natural Keys', link: '/events/natural-keys' },
            { text: 'Dynamic Consistency Boundary', link: '/events/dcb' },
            { text: 'Rewriting Events', link: '/events/rewriting' },
            { text: 'Upcasting Events', link: '/events/upcasting' },
            {
              text: 'Projections Overview', link: '/events/projections/', collapsed: true, items: [
                { text: 'Single Stream Projections', link: '/events/projections/single-stream-projections' },
                { text: 'Multi Stream Projections', link: '/events/projections/multi-stream-projections' },
                { text: 'Event Projections', link: '/events/projections/event-projections' },
                { text: 'Live Aggregations', link: '/events/projections/live-aggregates' },
                { text: 'Inline Projections', link: '/events/projections/inline' },
                { text: 'Flat Table Projections', link: '/events/projections/flat' },
                { text: 'Composite Projections', link: '/events/projections/composite' },
                { text: 'Asynchronous Projections', link: '/events/projections/async-daemon' },
                { text: 'EF Core Projections', link: '/events/projections/efcore' },
                { text: 'Container-Scoped Projections', link: '/events/projections/container-scoped' },
                { text: 'ProjectLatest — Include Pending Events', link: '/events/projections/project-latest' },
                { text: 'Side Effects', link: '/events/projections/side-effects' },
              ]
            },
            { text: 'Event Subscriptions', link: '/events/subscriptions' },
            { text: 'Multi-Tenancy', link: '/events/multitenancy' },
          ]
        },
        {
          text: 'Testing',
          collapsed: true,
          items: [
            { text: 'Integration Testing', link: '/testing/integration' },
          ]
        },
        {
          text: 'Diagnostics',
          collapsed: true,
          items: [
            { text: 'Diagnostics and Instrumentation', link: '/diagnostics' },
          ]
        },
        {
          text: 'Schema',
          collapsed: true,
          items: [
            { text: 'Database Management', link: '/schema/' },
            { text: 'How Documents are Stored', link: '/schema/storage' },
            { text: 'Schema Migrations', link: '/schema/migrations' },
            { text: 'Exporting Schema Definition', link: '/schema/exporting' },
            { text: 'Tearing Down Document Storage', link: '/schema/cleaning' },
          ]
        },
        {
          text: 'Migration Guide',
          link: '/migration-guide',
        },
      ]
    }
  },
  vite: {
    plugins: [llmstxt()],
    build: {
      chunkSizeWarningLimit: 3000
    }
  }
}

export default defineConfig(config)
