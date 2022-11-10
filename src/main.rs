mod custom_redis;

use std::{ops::DerefMut, sync::Arc};

use axum::{
    extract::{
        ws::{WebSocket, WebSocketUpgrade},
        Path,
    },
    response::Response,
    routing::get,
    Extension, Router,
};

use r2d2_redis::{
    r2d2::{Pool, PooledConnection},
    redis::{self, Commands, RedisError},
    RedisConnectionManager,
};
use tokio::task;
#[tokio::main]
async fn main() {
    let database_pool = custom_redis::connect();
    let shared_database_pool = Arc::new(database_pool);
    let ext = Extension(shared_database_pool);

    // build our application with a single route
    let app = Router::new()
        .route("/server/:server_id/image", get(get_handler).layer(&ext))
        .route("/server/:server_id/ws", get(ws_handler).layer(&ext));

    // run it with hyper on localhost:3000
    axum::Server::bind(&"0.0.0.0:3000".parse().unwrap())
        .serve(app.into_make_service())
        .await
        .unwrap();
}

async fn get_handler(
    Extension(shared_database_pool): Extension<Arc<Pool<RedisConnectionManager>>>,
    Path(server_id): Path<String>,
) -> Vec<u8> {
    let database_pool = Arc::clone(&shared_database_pool);
    let mut con = database_pool.get().unwrap();
    let Keys {
        image: image_key, ..
    } = get_server_keys(server_id);
    let old_image: Option<Vec<u8>> = con.get(&image_key).unwrap();

    match old_image {
        Some(image) => image,
        None => {
            let _foo: Result<i32, r2d2_redis::redis::RedisError> = con.set(
                &image_key,
                (0..WIDTH * HEIGHT).map(|_| "\0").collect::<String>(),
            );
            let new_image: Option<Vec<u8>> = con.get(&image_key).unwrap();
            return new_image.unwrap();
        }
    }
}

async fn ws_handler(
    shared_database_pool: Extension<Arc<Pool<RedisConnectionManager>>>,
    server_id: Path<String>,
    ws: WebSocketUpgrade,
) -> Response {
    ws.on_upgrade(|socket| handle_upgrade(shared_database_pool, server_id, socket))
}

async fn handle_upgrade(
    Extension(shared_database_pool): Extension<Arc<Pool<RedisConnectionManager>>>,
    Path(server_id): Path<String>,
    mut socket: WebSocket,
) -> () {
    let database_pool = Arc::clone(&shared_database_pool);
    let Keys {
        image: image_key,
        pubsub: pubsub_key,
    } = get_server_keys(server_id);
    let mut con = database_pool.get().unwrap();

    let write_thread = task::spawn(async move {
        let mut pubsub = con.as_pubsub();
        pubsub.subscribe(pubsub_key).unwrap();
        loop {
            let msg = pubsub.get_message().unwrap();
            let payload: String = msg.get_payload().unwrap();
            println!("channel '{}': {}", msg.get_channel_name(), payload);
        }
    });

    while let Some(result) = socket.recv().await {
        match result {
            Ok(msg) => {
                let binary = msg.into_data();
                let x = u16::from_be_bytes([binary[0], binary[1]]);
                let y = u16::from_be_bytes([binary[2], binary[3]]);
                let color = binary[4];
                let mut update_con = database_pool.get().unwrap();
                let _a: () = set_color(&mut update_con, &image_key, x, y, color).unwrap();
            }
            Err(e) => {
                println!("Error: {}", e);
                break;
            }
        }

        // if socket.send(msg).await.is_err() {
        //     // client disconnected
        //     return;
        // }
    }

    let result = write_thread.await.unwrap();
}

fn get_server_keys(server_id: String) -> Keys {
    Keys {
        image: format!("server:{}:image", server_id),
        pubsub: format!("server:{}:pubsub", server_id),
    }
}

struct Keys {
    image: String,
    pubsub: String,
}

fn set_color(
    connection: &mut PooledConnection<RedisConnectionManager>,
    key: &String,
    x: u16,
    y: u16,
    color: u8,
) -> Result<(), RedisError> {
    let offset = y * WIDTH + x;
    redis::cmd("BITFIELD")
        .arg(key)
        .arg("SET")
        .arg("u8")
        .arg(offset)
        .arg(color)
        .query(connection.deref_mut())?;
    return Ok(());
}

const WIDTH: u16 = 1920;
const HEIGHT: u16 = 1080;
