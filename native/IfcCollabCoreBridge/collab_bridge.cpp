#include "collab_bridge.h"

#include "collab/client.h"
#include "collab/codec.h"
#include "collab/json.h"

#include <filesystem>
#include <fstream>
#include <functional>
#include <memory>
#include <string>
#include <system_error>
#include <unordered_map>
#include <utility>
#include <vector>

using collab::json::Value;

namespace
{
// ---- JSON helpers -------------------------------------------------------------------------------

Value StringArray(const std::vector<std::string>& Items)
{
	Value Out = Value::MakeArray();
	for (const std::string& S : Items)
	{
		Out.Push(S);
	}
	return Out;
}

std::vector<std::string> ReadStrings(const Value& Arr)
{
	std::vector<std::string> Out;
	for (const Value& V : Arr.Items())
	{
		if (V.IsString())
		{
			Out.push_back(V.AsString());
		}
	}
	return Out;
}

Value Vec3Value(const collab::Vec3& V) { return Value::MakeObject().Set("x", V.X).Set("y", V.Y).Set("z", V.Z); }

collab::Vec3 ReadVec3(const Value& Obj) { return { Obj["x"].AsNumber(), Obj["y"].AsNumber(), Obj["z"].AsNumber() }; }

Value CameraValue(const collab::Camera& Cam)
{
	if (!Cam.Valid)
	{
		return Value();
	}
	return Value::MakeObject()
		.Set("location", Vec3Value(Cam.Location))
		.Set("direction", Vec3Value(Cam.Direction))
		.Set("up", Vec3Value(Cam.Up))
		.Set("fieldOfView", Cam.FieldOfView);
}

collab::Camera ReadCamera(const Value& Obj)
{
	collab::Camera Cam;
	if (!Obj.IsObject())
	{
		return Cam;
	}
	Cam.Location = ReadVec3(Obj["location"]);
	Cam.Direction = ReadVec3(Obj["direction"]);
	Cam.Up = ReadVec3(Obj["up"]);
	Cam.FieldOfView = Obj["fieldOfView"].AsNumber(60.0);
	Cam.Valid = !Cam.Direction.IsNearlyZero();
	return Cam;
}

bool Base64Decode(const std::string& In, std::vector<uint8_t>& Out)
{
	auto Digit = [](char C) -> int
	{
		if (C >= 'A' && C <= 'Z') return C - 'A';
		if (C >= 'a' && C <= 'z') return C - 'a' + 26;
		if (C >= '0' && C <= '9') return C - '0' + 52;
		if (C == '+' || C == '-') return 62;
		if (C == '/' || C == '_') return 63;
		return -1;
	};
	Out.clear();
	Out.reserve(In.size() * 3 / 4);
	uint32_t Acc = 0;
	int Bits = 0;
	for (const char C : In)
	{
		if (C == '=' || C == '\r' || C == '\n' || C == ' ')
		{
			continue;
		}
		const int D = Digit(C);
		if (D < 0)
		{
			return false;
		}
		Acc = (Acc << 6) | static_cast<uint32_t>(D);
		Bits += 6;
		if (Bits >= 8)
		{
			Bits -= 8;
			Out.push_back(static_cast<uint8_t>((Acc >> Bits) & 0xFF));
		}
	}
	return true;
}

/** A viewpoint from the host: {camera, png (base64), selection}. */
collab::ViewpointInput ReadViewpoint(const Value& Obj)
{
	collab::ViewpointInput Vp;
	Vp.Cam = ReadCamera(Obj["camera"]);
	Base64Decode(Obj["png"].AsString(), Vp.SnapshotPng);
	Vp.Selection = ReadStrings(Obj["selection"]);
	return Vp;
}

std::filesystem::path FsPath(const std::string& Utf8) { return std::filesystem::u8path(Utf8); }

// ---- The Platform the Core runs on ----------------------------------------------------------------

class HostPlatform final : public collab::Platform
{
public:
	HostPlatform(const collab_host& InHost, std::string InSession, std::string InCacheDir)
		: Host(InHost), SessionJson(std::move(InSession)), CacheDir(std::move(InCacheDir))
	{
	}

	const collab_host Host;

	void Send(collab::HttpRequest Request, ResponseCallback OnDone) override { Start(std::move(Request), std::string(), std::move(OnDone)); }

	uint64_t Download(collab::HttpRequest Request, std::string FilePath, ProgressCallback, ResponseCallback OnDone) override
	{
		return Start(std::move(Request), std::move(FilePath), std::move(OnDone));
	}

	void Cancel(uint64_t DownloadId) override { Host.cancel(Host.context, DownloadId); }

	uint64_t PostDelayed(double DelaySeconds, std::function<void()> Fn) override
	{
		const uint64_t Id = NextId++;
		Timers.emplace(Id, std::move(Fn));
		Host.post_delayed(Host.context, Id, DelaySeconds);
		return Id;
	}

	void CancelTimer(uint64_t TimerId) override
	{
		if (Timers.erase(TimerId) > 0)
		{
			Host.cancel_timer(Host.context, TimerId);
		}
	}

	collab::SessionRecord LoadSession() override
	{
		collab::SessionRecord Record;
		Value Root;
		if (!SessionJson.empty() && collab::json::Parse(SessionJson, Root))
		{
			Record.ServerUrl = collab::json::Str(Root, "serverUrl");
			Record.Email = collab::json::Str(Root, "email");
			Record.RefreshToken = collab::json::Str(Root, "refreshToken");
			Record.RecentServers = ReadStrings(Root["recentServers"]);
		}
		return Record;
	}

	void SaveSession(const collab::SessionRecord& Record) override
	{
		const std::string Json = collab::json::Write(Value::MakeObject()
														 .Set("serverUrl", Record.ServerUrl)
														 .Set("email", Record.Email)
														 .Set("refreshToken", Record.RefreshToken)
														 .Set("recentServers", StringArray(Record.RecentServers)));
		Host.save_session(Host.context, Json.c_str());
	}

	std::string CacheDirectory() override { return CacheDir; }

	int64_t FileSize(const std::string& Path) override
	{
		std::error_code Ec;
		const auto Size = std::filesystem::file_size(FsPath(Path), Ec);
		return Ec ? -1 : static_cast<int64_t>(Size);
	}

	bool ReadTextFile(const std::string& Path, std::string& Out) override
	{
		std::ifstream In(FsPath(Path), std::ios::binary);
		if (!In)
		{
			return false;
		}
		Out.assign(std::istreambuf_iterator<char>(In), std::istreambuf_iterator<char>());
		return true;
	}

	bool WriteTextFile(const std::string& Path, std::string_view Data) override
	{
		std::error_code Ec;
		std::filesystem::create_directories(FsPath(Path).parent_path(), Ec);
		std::ofstream Out(FsPath(Path), std::ios::binary | std::ios::trunc);
		if (!Out)
		{
			return false;
		}
		Out.write(Data.data(), static_cast<std::streamsize>(Data.size()));
		return static_cast<bool>(Out);
	}

	bool RemoveFile(const std::string& Path) override
	{
		std::error_code Ec;
		return std::filesystem::remove(FsPath(Path), Ec);
	}

	bool RenameFile(const std::string& From, const std::string& To) override
	{
		std::error_code Ec;
		std::filesystem::rename(FsPath(From), FsPath(To), Ec);
		return !Ec;
	}

	bool MakeDirectory(const std::string& Path) override
	{
		std::error_code Ec;
		std::filesystem::create_directories(FsPath(Path), Ec);
		return !Ec;
	}

	void Log(collab::LogLevel Level, const std::string& Message) override { Host.log(Host.context, static_cast<int>(Level), Message.c_str()); }

	void Complete(uint64_t Id, const char* ResponseJson, const uint8_t* Body, size_t BodySize)
	{
		const auto Found = Requests.find(Id);
		if (Found == Requests.end())
		{
			return;
		}
		ResponseCallback OnDone = std::move(Found->second);
		Requests.erase(Found);

		collab::HttpResponse Response;
		Value Root;
		if (ResponseJson && collab::json::Parse(ResponseJson, Root))
		{
			Response.Connected = Root["connected"].AsBool();
			Response.Cancelled = Root["cancelled"].AsBool();
			Response.TimedOut = Root["timedOut"].AsBool();
			Response.Status = Root["status"].AsInt();
			for (const Value& H : Root["headers"].Items())
			{
				Response.Headers.emplace_back(H[0].AsString(), H[1].AsString());
			}
		}
		if (Body && BodySize > 0)
		{
			Response.Body.assign(reinterpret_cast<const char*>(Body), BodySize);
		}
		OnDone(std::move(Response));
	}

	void Fire(uint64_t Id)
	{
		const auto Found = Timers.find(Id);
		if (Found == Timers.end())
		{
			return;
		}
		std::function<void()> Fn = std::move(Found->second);
		Timers.erase(Found);
		Fn();
	}

private:
	uint64_t Start(collab::HttpRequest Request, std::string FilePath, ResponseCallback OnDone)
	{
		const uint64_t Id = NextId++;
		Value Headers = Value::MakeArray();
		for (const auto& [Name, Val] : Request.Headers)
		{
			Headers.Push(Value::MakeArray().Push(Name).Push(Val));
		}
		Value Meta = Value::MakeObject()
						 .Set("method", Request.Method)
						 .Set("url", Request.Url)
						 .Set("headers", std::move(Headers))
						 .Set("timeout", Request.TimeoutSeconds)
						 .Set("activityTimeout", Request.ActivityTimeoutSeconds);
		if (!FilePath.empty())
		{
			Meta.Set("file", FilePath);
		}
		Requests.emplace(Id, std::move(OnDone));
		const std::string Json = collab::json::Write(Meta);
		Host.send(Host.context, Id, Json.c_str(), reinterpret_cast<const uint8_t*>(Request.Body.data()), Request.Body.size());
		return Id;
	}

	std::string SessionJson;
	std::string CacheDir;
	uint64_t NextId = 1;
	std::unordered_map<uint64_t, ResponseCallback> Requests;
	std::unordered_map<uint64_t, std::function<void()>> Timers;
};

collab::ClientConfig ReadConfig(const char* ConfigJson)
{
	collab::ClientConfig Config;
	Config.ClientName = "open-bim-revit";
	Value Root;
	if (ConfigJson && collab::json::Parse(ConfigJson, Root))
	{
		if (Root["clientName"].IsString())
		{
			Config.ClientName = Root["clientName"].AsString();
		}
		Config.DefaultServerUrl = collab::json::Str(Root, "defaultServerUrl");
		Config.RequestTimeoutSeconds = Root["requestTimeout"].AsNumber(Config.RequestTimeoutSeconds);
	}
	return Config;
}
} // namespace

struct collab_bridge
{
	collab_bridge(const collab_host& Host, const char* ConfigJson, std::string Session, std::string CacheDir)
		: Platform(Host, std::move(Session), std::move(CacheDir)), Client(Platform, ReadConfig(ConfigJson))
	{
		Client.SetOnlineChanged([this](bool bOnline) { Platform.Host.online_changed(Platform.Host.context, bOnline ? 1 : 0); });
	}

	// Declared first so it outlives the Client, which cancels its requests through it.
	HostPlatform Platform;
	collab::Client Client;
	std::string QueryResult;

	std::string SnapshotUrl(const std::string& File) const
	{
		return File.empty() ? std::string() : Client.ResolveUrl(collab::codec::SnapshotPath(File));
	}

	Value IssueValue(const collab::Issue& I) const
	{
		Value Comments = Value::MakeArray();
		for (const collab::Comment& C : I.Comments)
		{
			Comments.Push(Value::MakeObject()
							  .Set("author", C.Author)
							  .Set("date", C.Date)
							  .Set("text", C.Text)
							  .Set("viewpointGuid", C.ViewpointGuid)
							  .Set("snapshotUrl", SnapshotUrl(C.Snapshot))
							  .Set("camera", CameraValue(C.Cam)));
		}
		Value Out = Value::MakeObject()
						.Set("id", I.Id)
						.Set("guid", I.Guid)
						.Set("index", I.Index)
						.Set("title", I.Title)
						.Set("topicType", I.TopicType)
						.Set("topicStatus", I.TopicStatus)
						.Set("priority", I.Priority)
						.Set("assignedTo", I.AssignedTo)
						.Set("description", I.Description)
						.Set("stage", I.Stage)
						.Set("labels", StringArray(I.Labels))
						.Set("dueDate", I.DueDate)
						.Set("creationAuthor", I.CreationAuthor)
						.Set("creationDate", I.CreationDate)
						.Set("modifiedAuthor", I.ModifiedAuthor)
						.Set("modifiedDate", I.ModifiedDate)
						.Set("snapshotViewpointGuid", I.SnapshotViewpointGuid)
						.Set("snapshotUrl", SnapshotUrl(I.Snapshot))
						.Set("camera", CameraValue(I.Cam))
						.Set("comments", std::move(Comments));
		if (I.HasPin)
		{
			Out.Set("pin", Vec3Value(I.Pin));
		}
		return Out;
	}

	Value ViewValue(const collab::SavedView& V) const
	{
		return Value::MakeObject()
			.Set("id", V.Id)
			.Set("guid", V.Guid)
			.Set("name", V.Name)
			.Set("creationAuthor", V.CreationAuthor)
			.Set("createdAt", V.CreatedAt)
			.Set("snapshotUrl", SnapshotUrl(V.Snapshot))
			.Set("isStarting", V.bIsStarting)
			.Set("camera", CameraValue(V.Cam));
	}

	void Answer(uint64_t CallId, bool bOk, const std::string& Error, Value Payload = Value::MakeObject(), const std::string& Data = std::string())
	{
		if (!Payload.IsObject())
		{
			Payload = Value::MakeObject();
		}
		Payload.Set("ok", bOk).Set("error", Error);
		const std::string Json = collab::json::Write(Payload);
		Platform.Host.result(Platform.Host.context, CallId, Json.c_str(), reinterpret_cast<const uint8_t*>(Data.data()), Data.size());
	}

	void Call(uint64_t CallId, const std::string& Op, const Value& Args)
	{
		using namespace collab;
		const std::string ProjectId = json::Str(Args, "projectId");
		const auto Done = [this, CallId](bool bOk, const std::string& Error) { Answer(CallId, bOk, Error); };

		if (Op == "login")
		{
			Client.Login(json::Str(Args, "server"), json::Str(Args, "email"), json::Str(Args, "password"), Done);
		}
		else if (Op == "resume")
		{
			Client.ResumeSession(Done);
		}
		else if (Op == "checkConnectivity")
		{
			Client.CheckConnectivity(Done);
		}
		else if (Op == "projects")
		{
			Client.FetchProjects([this, CallId](bool bOk, const std::string& Error, std::vector<Project> Projects)
				{
					Value Items = Value::MakeArray();
					for (const Project& P : Projects)
					{
						Items.Push(Value::MakeObject()
									   .Set("id", P.Id)
									   .Set("name", P.Name)
									   .Set("description", P.Description)
									   .Set("role", P.Role)
									   .Set("modelCount", P.ModelCount));
					}
					Answer(CallId, bOk, Error, Value::MakeObject().Set("items", std::move(Items)));
				});
		}
		else if (Op == "views")
		{
			Client.FetchViews(ProjectId, [this, CallId](bool bOk, const std::string& Error, std::vector<SavedView> Views)
				{
					Value Items = Value::MakeArray();
					for (const SavedView& V : Views)
					{
						Items.Push(ViewValue(V));
					}
					Answer(CallId, bOk, Error, Value::MakeObject().Set("items", std::move(Items)));
				});
		}
		else if (Op == "issues")
		{
			Client.FetchIssues(ProjectId, [this, CallId](bool bOk, const std::string& Error, std::vector<Issue> Issues)
				{
					Value Items = Value::MakeArray();
					for (const Issue& I : Issues)
					{
						Items.Push(IssueValue(I));
					}
					Answer(CallId, bOk, Error, Value::MakeObject().Set("items", std::move(Items)));
				});
		}
		else if (Op == "createIssue")
		{
			NewIssue N;
			N.Title = json::Str(Args, "title");
			N.Description = json::Str(Args, "description");
			N.TopicType = json::Str(Args, "topicType");
			N.Priority = json::Str(Args, "priority");
			N.Components = ReadStrings(Args["components"]);
			N.Viewpoint = ReadViewpoint(Args["viewpoint"]);
			Client.CreateIssue(ProjectId, N, [this, CallId](bool bOk, const std::string& Error, Issue I)
				{ Answer(CallId, bOk, Error, Value::MakeObject().Set("issue", IssueValue(I))); });
		}
		else if (Op == "updateIssue")
		{
			IssueUpdate U;
			const auto Field = [&Args](const char* Key, std::optional<std::string>& Slot)
			{
				const Value* V = Args.Find(Key);
				if (V && (V->IsString() || V->IsNull()))
				{
					Slot = V->AsString();
				}
			};
			Field("title", U.Title);
			Field("description", U.Description);
			Field("topicType", U.TopicType);
			Field("topicStatus", U.TopicStatus);
			Field("priority", U.Priority);
			Field("stage", U.Stage);
			Field("assignedTo", U.AssignedTo);
			Field("dueDate", U.DueDate);
			if (Args["labels"].IsArray())
			{
				U.Labels = ReadStrings(Args["labels"]);
			}
			Client.UpdateIssue(ProjectId, json::Str(Args, "issueId"), U, [this, CallId](bool bOk, const std::string& Error, Issue I)
				{ Answer(CallId, bOk, Error, Value::MakeObject().Set("issue", IssueValue(I))); });
		}
		else if (Op == "addComment")
		{
			const bool bHasViewpoint = Args["viewpoint"].IsObject();
			const ViewpointInput Vp = bHasViewpoint ? ReadViewpoint(Args["viewpoint"]) : ViewpointInput();
			Client.AddComment(ProjectId, json::Str(Args, "issueId"), json::Str(Args, "text"), bHasViewpoint ? &Vp : nullptr, Done);
		}
		else if (Op == "updateSnapshot")
		{
			std::vector<uint8_t> Png;
			if (!Base64Decode(Args["png"].AsString(), Png) || Png.empty())
			{
				Answer(CallId, false, "The image is empty.");
				return;
			}
			Client.UpdateViewpointSnapshot(ProjectId, json::Str(Args, "issueId"), json::Str(Args, "viewpointGuid"), Png,
				[this, CallId](bool bOk, const std::string& Error, std::string File)
				{ Answer(CallId, bOk, Error, Value::MakeObject().Set("snapshotUrl", SnapshotUrl(File))); });
		}
		else if (Op == "download")
		{
			Client.DownloadBytes(json::Str(Args, "url"), [this, CallId](bool bOk, const std::string& Error, std::string Bytes)
				{ Answer(CallId, bOk, Error, Value::MakeObject(), Bytes); });
		}
		else if (Op == "sync")
		{
			Client.SyncProject(ProjectId, [this, CallId](bool bOk, const std::string& Error, SyncSummary S)
				{
					Answer(CallId, bOk, Error,
						Value::MakeObject()
							.Set("applied", S.Applied + S.Overwrote + S.Duplicates)
							.Set("rejected", S.Rejected)
							.Set("gone", S.Gone)
							.Set("remaining", S.Remaining));
				});
		}
		else
		{
			Answer(CallId, false, "Unknown operation: " + Op);
		}
	}

	std::string Query(const std::string& Op, const Value& Args)
	{
		using namespace collab;
		Value Out = Value::MakeObject();
		if (Op == "state")
		{
			const User& U = Client.GetUser();
			Out.Set("loggedIn", Client.IsLoggedIn())
				.Set("hasSavedSession", Client.HasSavedSession())
				.Set("online", Client.IsOnline())
				.Set("serverUrl", Client.GetServerUrl())
				.Set("lastEmail", Client.GetLastEmail())
				.Set("recentServers", StringArray(Client.GetRecentServers()))
				.Set("user", Value::MakeObject().Set("id", U.Id).Set("email", U.Email).Set("name", U.Name));
		}
		else if (Op == "vocabulary")
		{
			Value Colors = Value::MakeObject();
			for (const char* S : codec::TopicStatuses)
			{
				Colors.Set(S, codec::StatusColorHex(S));
			}
			Out.Set("topicTypes", StringArray({ std::begin(codec::TopicTypes), std::end(codec::TopicTypes) }))
				.Set("topicStatuses", StringArray({ std::begin(codec::TopicStatuses), std::end(codec::TopicStatuses) }))
				.Set("priorities", StringArray({ std::begin(codec::Priorities), std::end(codec::Priorities) }))
				.Set("statusColors", std::move(Colors));
		}
		else if (Op == "pending")
		{
			Value Items = Value::MakeArray();
			for (const PendingChange& C : Client.PendingChanges(json::Str(Args, "projectId")))
			{
				Items.Push(Value::MakeObject()
							   .Set("opId", C.OpId)
							   .Set("entity", ToString(C.Entity))
							   .Set("action", ToString(C.Action))
							   .Set("label", C.Label)
							   .Set("error", C.Error));
			}
			Out.Set("items", std::move(Items));
		}
		else if (Op == "logout")
		{
			Client.Logout();
		}
		else if (Op == "forgetServer")
		{
			Client.ForgetRecentServer(json::Str(Args, "server"));
		}
		else if (Op == "resolveUrl")
		{
			Out.Set("url", Client.ResolveUrl(json::Str(Args, "path")));
		}
		else if (Op == "normalizeServer")
		{
			Out.Set("url", codec::NormalizeServerUrl(json::Str(Args, "server")));
		}
		else
		{
			Out.Set("error", "Unknown query: " + Op);
		}
		return json::Write(Out);
	}
};

namespace
{
Value ParseArgs(const char* ArgsJson)
{
	Value Args;
	if (!ArgsJson || !collab::json::Parse(ArgsJson, Args) || !Args.IsObject())
	{
		Args = Value::MakeObject();
	}
	return Args;
}
} // namespace

extern "C" {

COLLAB_BRIDGE_API int COLLAB_BRIDGE_CALL collab_bridge_abi_version(void)
{
	return COLLAB_BRIDGE_ABI_VERSION;
}

COLLAB_BRIDGE_API collab_bridge* COLLAB_BRIDGE_CALL collab_bridge_create(const collab_host* host, const char* config_json, const char* session_json,
	const char* cache_dir)
{
	if (!host || !host->send || !host->cancel || !host->post_delayed || !host->cancel_timer || !host->save_session || !host->log || !host->result
		|| !host->online_changed)
	{
		return nullptr;
	}
	return new collab_bridge(*host, config_json, session_json ? session_json : "", cache_dir ? cache_dir : "");
}

COLLAB_BRIDGE_API void COLLAB_BRIDGE_CALL collab_bridge_destroy(collab_bridge* bridge)
{
	delete bridge;
}

COLLAB_BRIDGE_API void COLLAB_BRIDGE_CALL collab_bridge_complete_request(collab_bridge* bridge, uint64_t request_id, const char* response_json,
	const uint8_t* body, size_t body_size)
{
	if (bridge)
	{
		bridge->Platform.Complete(request_id, response_json, body, body_size);
	}
}

COLLAB_BRIDGE_API void COLLAB_BRIDGE_CALL collab_bridge_fire_timer(collab_bridge* bridge, uint64_t timer_id)
{
	if (bridge)
	{
		bridge->Platform.Fire(timer_id);
	}
}

COLLAB_BRIDGE_API void COLLAB_BRIDGE_CALL collab_bridge_call(collab_bridge* bridge, uint64_t call_id, const char* op, const char* args_json)
{
	if (bridge)
	{
		bridge->Call(call_id, op ? op : "", ParseArgs(args_json));
	}
}

COLLAB_BRIDGE_API const char* COLLAB_BRIDGE_CALL collab_bridge_query(collab_bridge* bridge, const char* op, const char* args_json)
{
	if (!bridge)
	{
		return "{}";
	}
	bridge->QueryResult = bridge->Query(op ? op : "", ParseArgs(args_json));
	return bridge->QueryResult.c_str();
}

} // extern "C"
