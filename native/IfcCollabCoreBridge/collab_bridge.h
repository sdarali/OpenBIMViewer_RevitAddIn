#pragma once

/*
 * A flat C interface over the IFC Collab Core's Client (../IFCCollabCore), for hosts that can't
 * use its C++ API directly, such as the .NET Revit add-in. Data crosses as UTF-8 JSON; binary
 * bodies (HTTP payloads, images) cross as pointer + size.
 *
 * Threading: like the Core itself, a bridge is used from one thread (the host's logic thread),
 * and the host must answer every callback on that same thread. Callbacks can run during any
 * bridge call, so a host must not call back into the bridge synchronously from inside one;
 * it answers later (e.g. through its dispatcher).
 *
 * Strings passed to callbacks are only valid during the callback.
 */

#include <stddef.h>
#include <stdint.h>

#if defined(_WIN32)
#if defined(COLLAB_BRIDGE_BUILD)
#define COLLAB_BRIDGE_API __declspec(dllexport)
#else
#define COLLAB_BRIDGE_API __declspec(dllimport)
#endif
#define COLLAB_BRIDGE_CALL __cdecl
#else
#define COLLAB_BRIDGE_API __attribute__((visibility("default")))
#define COLLAB_BRIDGE_CALL
#endif

/* Bumped whenever a signature or callback changes, so a host can refuse a mismatched DLL. */
#define COLLAB_BRIDGE_ABI_VERSION 1

#ifdef __cplusplus
extern "C" {
#endif

typedef struct collab_bridge collab_bridge;

typedef struct collab_host
{
	void* context;

	/*
	 * Perform an HTTP request and answer with collab_bridge_complete_request(request_id, ...).
	 * request_json: {"method","url","headers":[[name,value],...],"timeout":seconds,
	 * "activityTimeout":seconds, "file":path?}. With "file", the response body is written to
	 * that file instead of being returned.
	 */
	void(COLLAB_BRIDGE_CALL* send)(void* context, uint64_t request_id, const char* request_json, const uint8_t* body, size_t body_size);
	/* Abort a request; it still completes, with "cancelled": true. */
	void(COLLAB_BRIDGE_CALL* cancel)(void* context, uint64_t request_id);
	/* Call collab_bridge_fire_timer(timer_id) once, after delay_seconds. */
	void(COLLAB_BRIDGE_CALL* post_delayed)(void* context, uint64_t timer_id, double delay_seconds);
	void(COLLAB_BRIDGE_CALL* cancel_timer)(void* context, uint64_t timer_id);
	/* Persist the session ({"serverUrl","email","refreshToken","recentServers":[...]}) securely. */
	void(COLLAB_BRIDGE_CALL* save_session)(void* context, const char* session_json);
	/* level: 0 verbose, 1 info, 2 warning, 3 error. */
	void(COLLAB_BRIDGE_CALL* log)(void* context, int level, const char* message);
	/* The answer to collab_bridge_call: {"ok":bool,"error":string,...}; data carries bytes for "download". */
	void(COLLAB_BRIDGE_CALL* result)(void* context, uint64_t call_id, const char* result_json, const uint8_t* data, size_t data_size);
	/* The server became reachable (1) or unreachable (0). */
	void(COLLAB_BRIDGE_CALL* online_changed)(void* context, int online);
} collab_host;

COLLAB_BRIDGE_API int COLLAB_BRIDGE_CALL collab_bridge_abi_version(void);

/*
 * config_json: {"clientName","defaultServerUrl","requestTimeout"}; session_json: what
 * save_session last stored, or null; cache_dir: UTF-8 folder for the offline cache and outbox.
 * The host struct is copied.
 */
COLLAB_BRIDGE_API collab_bridge* COLLAB_BRIDGE_CALL collab_bridge_create(const collab_host* host, const char* config_json, const char* session_json,
	const char* cache_dir);
/* No callbacks run after this returns, except the cancels it issues for requests still in flight. */
COLLAB_BRIDGE_API void COLLAB_BRIDGE_CALL collab_bridge_destroy(collab_bridge* bridge);

/*
 * response_json: {"connected":bool,"cancelled":bool,"timedOut":bool,"status":int,
 * "headers":[[name,value],...]}. body is the response body (ignored for "file" requests).
 */
COLLAB_BRIDGE_API void COLLAB_BRIDGE_CALL collab_bridge_complete_request(collab_bridge* bridge, uint64_t request_id, const char* response_json,
	const uint8_t* body, size_t body_size);
COLLAB_BRIDGE_API void COLLAB_BRIDGE_CALL collab_bridge_fire_timer(collab_bridge* bridge, uint64_t timer_id);

/*
 * Start an asynchronous operation; its answer arrives through host->result(call_id). Operations:
 *   login {server,email,password}   resume {}            projects {}
 *   views {projectId}               issues {projectId}   createIssue {projectId,title,description,topicType,priority,components,viewpoint}
 *   updateIssue {projectId,issueId,<fields>}             addComment {projectId,issueId,text,viewpoint?}
 *   updateSnapshot {projectId,issueId,viewpointGuid,png}  download {url}
 *   sync {projectId}                checkConnectivity {}
 * A viewpoint is {camera,png,selection}; png is base64. Unknown operations answer ok:false.
 */
COLLAB_BRIDGE_API void COLLAB_BRIDGE_CALL collab_bridge_call(collab_bridge* bridge, uint64_t call_id, const char* op, const char* args_json);

/*
 * Synchronous queries and actions: state, vocabulary, pending {projectId}, logout,
 * forgetServer {server}, resolveUrl {path}. The returned JSON is owned by the bridge and stays
 * valid until the next call on it.
 */
COLLAB_BRIDGE_API const char* COLLAB_BRIDGE_CALL collab_bridge_query(collab_bridge* bridge, const char* op, const char* args_json);

#ifdef __cplusplus
}
#endif
