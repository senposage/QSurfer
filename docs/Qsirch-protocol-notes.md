# Qsirch Protocol Notes

This is a clean-room interoperability reference for QSurfer. It records observed
request and response behavior from Qsirch diagnostics and the installed Windows
client. Do not copy QNAP implementation code into this project.

## API Root And Authentication

- API root: `/qsirch/<api-version>/api`, normally `/qsirch/latest/api`.
- Login accepts an account, password, optional `remember_me`, and optional
  language/application identifiers.
- A successful login supplies a Qsirch session identifier (`qqs_sid`) and the
  authenticated NAS username and administrator flag.
- The native client can additionally receive an `auth_key` when it identifies
  itself with an application id and version. QSurfer does not rely on that
  private-client mechanism.
- The API returns `401` when its Qsirch session expires. Re-authenticate once
  and retry the interrupted request rather than repeatedly logging in.

## Search

### File Search

- Route: `GET /search`.
- Important parameters observed: `q`, `limit`, `offset`, `sort`, `store_history`.
- A bare `q` is broad/full-text search. It can return a document whose name does
  not match if indexed content matches.
- `q=name:"term"` performs server-side filename targeting and is the QSurfer
  default when **Search contents** is disabled.
- Use QSurfer's client exact-match check for whole-token matching. The NAS
  query grammar is broader and version-dependent.

### Folder Search

- Route: `GET /search-dirs`.
- Important parameters observed: `q`, `limit`, `show_hidden`, `show_recycled`,
  `show_folder_type`.
- The endpoint applies the authenticated Qsirch user's allowed directories,
  group ids, hidden/recycle preferences, and forbidden-directory rules.
- It returns `items` and `total`. A directory item includes `name` and
  `display`; `show_folder_type` adds a remote/local type indicator.
- The server allows a maximum `limit` of 100. A request for 500 returns `400`.
- It accepts plain or quoted terms. `name:"term"` does not produce the same
  folder matching behavior and should not be used here.
- The endpoint can stall for many minutes under server load. QSurfer permits
  only one in-flight folder request and never blocks file-result painting on it.

### Async Search

- Routes: `GET|POST /async-search` and
  `GET /async-search-resp/<context-id>/<start>/<offset>`.
- The initial call creates a server-side context; later calls page its result.
- This is polling/paged retrieval, not a server-push stream. Keep direct
  `/search` as the primary fast-result path. Async search is a candidate for
  resumable or background work only.

## Explorer-Style Browse

- Route: `GET /list-files`.
- Observed parameters: `path`, `sort`, `limit`, `offset`.
- Supported server sort fields include `filename`, `filesize`, `filetype`,
  `mt`, `privilege`, `owner`, and `group`.
- The result includes an asynchronous context and a `list-files-resp` route
  for subsequent pages. Items include resolved path information and action
  descriptors, subject to Qsirch access checks.
- This is the preferred future path for browsing NAS content without an SMB
  drive mapping. SMB/mapped paths should remain the first choice for opening
  files in Windows.

## Item Actions, Preview, And Metadata

- Route: `/qusion-item` with actions including `thumbnail`, `open`, `download`,
  `share`, and `preview`.
- Preview routes include `/preview/image`, `/preview/media`,
  `/preview/media/action-uri`, and text-content preview support.
- Preview and thumbnail actions incorporate file path, name, modification time,
  and a session token. They are descriptors/URLs, not a promise of a rendered
  native document preview.
- Metadata uses `/metadata` followed by `/metadata/<object-id>` while extraction
  completes.
- The Windows client includes specialized thumbnail handlers, including PDF and
  File Station handlers. Shell previews remain QSurfer's preferred Windows
  rendering path unless a NAS action gives a usable full-resolution asset.

## More Like This

- The native client exposes a `more-like-this` namespace and an item-specific
  route. It can start from an object id or a file path and name.
- This is an OpenSearch More-Like-This query, not a simple same-extension or
  filename lookup. The server derives indexed fields from the selected item and
  returns normal result payloads, including item actions.
- Observed controls include `categories`, `plugins`, `extensions`, `limit`,
  `min_similarity`, `show_hidden`, `show_recycled`, `show_folders`,
  `show_absolute_path`, and `file_status`.
- The server normalizes returned scores against the highest-scoring result and
  removes items below `min_similarity`. It also applies the requesting user's
  directory permissions.
- Treat it as an experimental "find related documents" command until live
  testing establishes whether the indexed fields are useful for office work.

## Operational Constraints

- Qsirch/OpenSearch can reject work when its scroll-context limit is exhausted.
  Do not create overlapping long-lived async or folder searches.
- NAS diagnostics showed `/search-dirs` calls that completed after more than
  14 minutes. Cancellation in a client does not necessarily stop already
  started server work.
- Capture or share logs only after removing session identifiers, auth keys,
  auth tokens, passwords, and user-private paths where appropriate.
