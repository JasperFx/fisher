---
layout: home
sidebar: false

title: Fisher
titleTemplate: .NET Event Store and Document Database on SQLite

hero:
  name: Fisher
  text: .NET Event Store and Document Database on SQLite
  tagline: Marten-inspired event sourcing and document storage in a single file, with no database server at all
  image:
    src: /logo.png
    alt: Fisher logo
  actions:
    - theme: brand
      text: Get Started
      link: /introduction
    - theme: alt
      text: Why Fisher?
      link: /whitepaper
    - theme: alt
      text: Document DB
      link: /documents/
    - theme: alt
      text: Event Store
      link: /events/

features:
  - title: No server, one file
    details: Fisher runs inside your process. There is nothing to install, nothing to provision, and nothing to keep running — a store is a SQLite file you can copy, back up, or throw away.
  - title: Document Database
    details: A full document database with LINQ querying — joins, grouping, aggregates and both paging styles — plus soft deletes, patching, hierarchies, bulk insert and optimistic concurrency.
  - title: Event Store
    details: Event sourcing with every projection shape across every lifecycle, an async projection daemon, subscriptions, DCB tags, natural keys, masking and stream compacting.
footer: MIT Licensed | Copyright &copy; Jeremy D. Miller and contributors.
---
