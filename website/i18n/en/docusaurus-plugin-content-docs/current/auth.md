---
title: Access & Authentication
description: RBAC roles, project visibility, API tokens, and LDAP
sidebar_position: 6
---

# Access & Authentication

## Role model

Infinity CI ships with three roles:

| Role | Capabilities |
| --- | --- |
| Super admin | Everything, including user management, system settings, and agent administration |
| Admin | Project & job management, enable/disable, issuing enroll tokens |
| User | View visible projects, trigger runs, inspect logs |

## Project visibility

Each user is granted visibility into selected projects. **Filtering applies consistently to the REST API and real-time push** — a user never receives SignalR events for projects they can't see.

## Personal API tokens

Issue a personal API token from user settings for CLIs, scripts, and third-party integrations:

```bash
curl -H "Authorization: Bearer <token>" \
  http://ci.example.com:5000/api/jobs/my-task/runs
```

A token carries the same permissions as its user and can be revoked at any time.

## LDAP domain login

For enterprise intranets, Infinity CI supports LDAP authentication (e.g. Active Directory):

- **Database-backed config, hot reload**: configure the LDAP server, bind DN, and search base from the web admin page — no restart needed;
- **Automatic user provisioning**: domain accounts get a local user created on first successful login;
- **Group → Admin mapping**: members of a chosen AD group are automatically granted the admin role;
- **StartTLS support** and a **connection test diagnostic**: verify connectivity with one click and quickly pinpoint bind failures or wrong search bases.

Local and LDAP accounts coexist: the `admin` super account can always log in locally as a fallback if the directory service is down.
