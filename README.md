# Api
Api to retrieve and update images

## Routes

| Name                      | Description                                                                                                                                   |
| ------------------------- | --------------------------------------------------------------------------------------------------------------------------------------------- |
| /servers/{serverId}/image | Returns the image of the specified serverId, as an application/octet-stream where each byte represents the color of a pixel                   |
| /servers/{serverId}/ws    | The web socket route to send and receive pixel updates. Binary format 2 bytes for X location, 2 bytes for Y location and 1 byte for the color |

## Environment Variables

| Name               | Description                                      | Required | Default |
| ------------------ | ------------------------------------------------ | -------- | ------- |
| REDIS_HOST         | The Redis server hostname                        | Yes      |         |
| REDIS_PORT         | The Redis server port                            | Yes      |         |
| REDIS_USERNAME     | The username used to connect to the Redis server | No       | (empty) |
| REDIS_PASSWORD     | The password used to connect to the Redis server | No       | (empty) |
| RATE_LIMIT_SECONDS | The frequency of pixel edits by users in seconds | No       | 300     |

