# Api
Api to retrieve and update images

## Environment Variables
| Name                      | Description                                      | Required | Default    |
| ------------------------- | ------------------------------------------------ | -------- | ---------- |
| REDIS_HOST                | The Redis server hostname                        | Yes      |            |
| REDIS_PORT                | The Redis server port                            | Yes      |            |
| REDIS_USERNAME            | The username used to connect to the Redis server | No       | (empty)    |
| REDIS_PASSWORD            | The password used to connect to the Redis server | No       | (empty)    |
| CACHE_POOL_MAX_OPEN       | Maximum database connections open                | No       | 5          |
| CACHE_POOL_MIN_IDLE       | Minimum database connections open                | No       | 1          |
| CACHE_POOL_EXPIRE_SECONDS | Maximum database connection lifetime in seconds  | No       | 30 minutes |


## Running

### Redis
To start up a simple redis test database, use:
```bash
docker run --name r-place-redis --publish 6379:6379 --detach redis
```