# MobilityTicketing dossier

## Lecture 6: Redis

The Redis lab is implemented in [CacheLab](../database/redis/lecture06/CacheLab). The application reuses the lecture 5 MongoDB journey search and puts a cache-aside layer around it.

My test steps, observations and answers are in [the lecture 6 evidence](evidence/lecture06/README.md).

The seat counter Lua script is [decrement_if_positive.lua](../database/redis/lecture06/decrement_if_positive.lua). It is deliberately a Redis-only exercise, not a ticket reservation.
