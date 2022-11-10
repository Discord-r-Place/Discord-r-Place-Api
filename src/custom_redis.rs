use std::{env::var, time::Duration};

use r2d2_redis::{r2d2, RedisConnectionManager};

const DEFAULT_CACHE_POOL_MAX_OPEN: u32 = 5;
const DEFAULT_CACHE_POOL_MIN_IDLE: u32 = 1;
const DEFAULT_CACHE_POOL_EXPIRE_SECONDS: u64 = 60 * 30;

fn create_connection_string() -> Result<String, String> {
    let host = var("REDIS_HOST").map_err(|_e| "Missing environment variable REDIS_HOST")?;
    let port = var("REDIS_PORT").map_err(|_e| "Missing environment variable REDIS_PORT")?;
    let username = var("REDIS_USERNAME").ok();
    let password = var("REDIS_PASSWORD").ok();

    Ok(match (username, password) {
        (None, None) => {
            format!("redis://{}:{}/", host, port)
        }
        (Some(username), None) => {
            format!("redis://{}:@{}:{}/", username, host, port)
        }
        (None, Some(password)) => {
            format!("redis://:{}@{}:{}/", password, host, port)
        }
        (Some(username), Some(password)) => {
            format!("redis://{}:{}@{}:{}/", username, password, host, port)
        }
    })
}

pub fn connect() -> r2d2::Pool<RedisConnectionManager> {
    let connection_string = create_connection_string().unwrap();
    let manager = RedisConnectionManager::new(connection_string).unwrap();

    // TODO reuse this logic instead of duplicating it 4 times.
    let cache_pool_max_open: u32 = var("CACHE_POOL_MAX_OPEN")
        .map(|s| s.parse::<u32>().unwrap())
        .unwrap_or(DEFAULT_CACHE_POOL_MAX_OPEN);
    let cache_pool_min_idle: u32 = var("CACHE_POOL_MIN_IDLE")
        .map(|s| s.parse::<u32>().unwrap())
        .unwrap_or(DEFAULT_CACHE_POOL_MIN_IDLE);
    let cache_pool_expire_seconds: u64 = var("CACHE_POOL_EXPIRE_SECONDS")
        .map(|s| s.parse::<u64>().unwrap())
        .unwrap_or(DEFAULT_CACHE_POOL_EXPIRE_SECONDS);

    r2d2::Pool::builder()
        .max_size(cache_pool_max_open)
        .max_lifetime(Some(Duration::from_secs(cache_pool_expire_seconds)))
        .min_idle(Some(cache_pool_min_idle))
        .build(manager)
        .unwrap()
}
