// Constraint: navmesh reads live apart from the commands, so the state reply and the apply pipeline share one reader.

#include <windows.h>

#include <cfloat>
#include <cmath>
#include <cstddef>
#include <cctype>
#include <cstdint>
#include <cstdio>
#include <string>
#include <vector>

#include <EditorAPI/N/NavMesh.h>
#include <EditorAPI/T/TESForm.h>
#include <EditorAPI/T/TESObjectCELL.h>

#include "AgentEditorShared.h"
#include "AgentNavmeshRead.h"
#include "NavmeshDigest.h"

namespace ckagent
{
	namespace navmeshread
	{
		using namespace editor;
		using EditorCell = CKPE::Fallout4::EditorAPI::Forms::TESObjectCELL;
		using EditorForm = CKPE::Fallout4::EditorAPI::Forms::TESForm;
		using EditorNavMesh = CKPE::Fallout4::EditorAPI::Forms::NavMesh;

		// Constraint: a read must stay inside one committed region, so a stale pointer cannot fault the editor.
		bool Readable(const void* address, std::size_t bytes)
		{
			if (!address || !bytes) return false;

			MEMORY_BASIC_INFORMATION info{};
			if (!VirtualQuery(address, &info, sizeof info)) return false;
			if (info.State != MEM_COMMIT) return false;
			if (info.Protect & (PAGE_NOACCESS | PAGE_GUARD)) return false;

			const auto start = reinterpret_cast<std::uintptr_t>(address);
			const auto region = reinterpret_cast<std::uintptr_t>(info.BaseAddress) + info.RegionSize;
			return start + bytes <= region;
		}

		// Constraint: the read must cover every accessor below, so 0x180 is TESObjectCELL's items tail.
		constexpr std::size_t kCellReadable = 0x180;
		// Constraint: one reply must stay bounded, so the listing stops at 64 forms.
		constexpr std::uint32_t kMaxNavForms = 64;
		constexpr std::size_t kNavVerticesOffset = 0x40;
		constexpr std::size_t kNavTrianglesOffset = 0x58;
		constexpr std::size_t kNavArrayAllocOffset = 0x8;
		constexpr std::size_t kNavArraySizeOffset = 0x10;
		// Constraint: the array heads must be readable before either count is taken.
		constexpr std::size_t kNavReadable = 0x70;
		constexpr std::uint32_t kMaxNavElements = 2000000;
		// Constraint: the element is 24 bytes with its three vertex indices at +0, measured on every triangle across five navmeshes.
		constexpr std::size_t kNavTriangleStride = 24;
		constexpr std::size_t kNavVertexStride = 12;
		// Constraint: these element offsets were read off the creation path, so cover is addressed by name, never by guess.
		constexpr std::size_t kNavLinksOffset = 0x06;
		constexpr std::size_t kNavCoverHeightOffset = 0x0C;
		constexpr std::size_t kNavFlagsOffset = 0x10;
		constexpr std::uint32_t kNavCoverEdgeMask = 0x7;
		constexpr std::uint32_t kNavCoverEdgeShift = 16;
		// Constraint: an area read must stay bounded, so it stops at 32 MiB of geometry.
		constexpr std::size_t kMaxNavGeometryBytes = 32u * 1024u * 1024u;
		// Constraint: the cell's navmesh list is the BSTArray the accessor reads, and its head sits after the item array
		// at 0x78; the size is only written after the head has been proved to be that same array.
		constexpr std::size_t kCellNavMeshesOffset = 0x78;

		std::string HexId(std::uint32_t id)
		{
			char buf[16];
			std::snprintf(buf, sizeof buf, "%08X", id);
			return buf;
		}

		// Constraint: an array the editor emptied is not an array that could not be read, and a Recast pass empties the forms it replaces.
		enum class NavArrayRead
		{
			Unknown,
			Empty,
			Present
		};

		// Constraint: cover is per edge in the flags word, so each count is named after the link it sits on.
		struct NavCover
		{
			std::uint32_t edge01 = 0;
			std::uint32_t edge12 = 0;
			std::uint32_t edge20 = 0;
			std::uint32_t any = 0;
			std::uint32_t all = 0;
			std::uint32_t openEdges = 0;
			std::uint32_t heightSet = 0;
		};

		struct NavGeometry
		{
			bool known = false;
			bool empty = false;
			std::uint32_t vertices = 0;
			std::uint32_t triangles = 0;
			double area = 0;
			std::uint32_t areaTriangles = 0;
			NavCover cover;
			std::string verticesHash;
			std::string trianglesHash;
			bool hashed = false;
		};

		std::string GeometryName(NavArrayRead read)
		{
			switch (read)
			{
			case NavArrayRead::Empty: return "empty";
			case NavArrayRead::Present: return "read";
			default: return "unreadable";
			}
		}

		struct NavFormState
		{
			bool navmesh = false;
			int type = 0;
			std::string formId;
			std::string editorId;
			std::string parentCell;
			std::uint32_t navmeshId = 0;
			bool parentMatches = false;
			std::uint32_t vertices = 0;
			std::uint32_t triangles = 0;
			bool counts = false;
			std::string geometry = "unreadable";
			double area = 0;
			std::uint32_t areaTriangles = 0;
			bool areaOk = false;
			NavCover cover;
			bool coverOk = false;
			std::string verticesHash;
			std::string trianglesHash;
			bool hashed = false;
		};

		std::string JsonNumber(double value)
		{
			char buf[32];
			std::snprintf(buf, sizeof buf, "%.2f", value);
			return buf;
		}

		// Constraint: MSVC mis-lexes the escaped cover key literal in this file, so keys are built from Quote.
		std::string JsonKey(const char* name)
		{
			return std::string(",") + Quote(name) + ":";
		}

		std::string CoverJson(const NavCover& cover)
		{
			return "{\"edge01\":" + std::to_string(cover.edge01) +
				",\"edge12\":" + std::to_string(cover.edge12) +
				",\"edge20\":" + std::to_string(cover.edge20) +
				",\"any\":" + std::to_string(cover.any) +
				",\"all\":" + std::to_string(cover.all) +
				",\"openEdges\":" + std::to_string(cover.openEdges) +
				",\"heightSet\":" + std::to_string(cover.heightSet) + "}";
		}

		// Constraint: cover is reported per form as well as per cell, so several meshes can be compared one by one.
		std::string NavFormJson(const NavFormState& form)
		{
			return "{\"formId\":" + Quote(form.formId) + ",\"editorId\":" + Quote(form.editorId) +
				",\"type\":" + std::to_string(form.type) +
				",\"navmesh\":" + (form.navmesh ? "true" : "false") +
				",\"navmeshId\":" + std::to_string(form.navmeshId) +
				",\"parentCell\":" + Quote(form.parentCell) +
				",\"geometry\":" + Quote(form.geometry) +
				",\"vertices\":" + (form.counts ? std::to_string(form.vertices) : std::string("null")) +
				",\"triangles\":" + (form.counts ? std::to_string(form.triangles) : std::string("null")) +
				",\"area\":" + (form.areaOk ? JsonNumber(form.area) : std::string("null")) +
				",\"areaTriangles\":" + (form.areaOk ? std::to_string(form.areaTriangles) : std::string("null")) +
				",\"verticesHash\":" + (form.hashed ? Quote(form.verticesHash) : std::string("null")) +
				",\"trianglesHash\":" + (form.hashed ? Quote(form.trianglesHash) : std::string("null")) +
				JsonKey("cover") + (form.coverOk ? CoverJson(form.cover) : std::string("null")) + "}";
		}

		// Constraint: the count is taken before the buffer is touched, so a replaced navmesh never walks its old array.
		NavArrayRead ReadNavArraySize(const void* navmesh, std::size_t offset, std::uint32_t& size)
		{
			size = 0;
			if (!Readable(navmesh, kNavReadable)) return NavArrayRead::Unknown;

			const auto* base = static_cast<const std::uint8_t*>(navmesh);
			const void* buffer = *reinterpret_cast<const void* const*>(base + offset);
			const std::uint32_t alloc = *reinterpret_cast<const std::uint32_t*>(base + offset + kNavArrayAllocOffset);
			const std::uint32_t count = *reinterpret_cast<const std::uint32_t*>(base + offset + kNavArraySizeOffset);

			// Constraint: the editor leaves a zero count on a navmesh whose geometry a Recast pass replaced, which reads as zero geometry.
			if (count == 0) return NavArrayRead::Empty;
			if (count > kMaxNavElements || alloc < count) return NavArrayRead::Unknown;
			if (!buffer || !Readable(buffer, 0x10)) return NavArrayRead::Unknown;

			size = count;
			return NavArrayRead::Present;
		}

		// Constraint: the geometry is reported only when both arrays agree, so a half-replaced navmesh reports nothing.
		bool ReadNavGeometry(const void* navmesh, NavGeometry& out)
		{
			out = NavGeometry();

			std::uint32_t vertexCount = 0, triangleCount = 0;
			const NavArrayRead verticesRead = ReadNavArraySize(navmesh, kNavVerticesOffset, vertexCount);
			const NavArrayRead trianglesRead = ReadNavArraySize(navmesh, kNavTrianglesOffset, triangleCount);
			if (verticesRead == NavArrayRead::Unknown || trianglesRead == NavArrayRead::Unknown) return false;
			if (verticesRead != trianglesRead) return false;

			if (verticesRead == NavArrayRead::Empty)
			{
				out.known = true;
				out.empty = true;
				return true;
			}

			const auto* base = static_cast<const std::uint8_t*>(navmesh);
			const auto* vertices = *reinterpret_cast<const std::uint8_t* const*>(base + kNavVerticesOffset);
			const auto* elements = *reinterpret_cast<const std::uint8_t* const*>(base + kNavTrianglesOffset);
			if (!vertices || !elements) return false;

			const std::size_t vertexBytes = static_cast<std::size_t>(vertexCount) * kNavVertexStride;
			const std::size_t triangleBytes = static_cast<std::size_t>(triangleCount) * kNavTriangleStride;
			// Constraint: a bad count must not walk past the geometry budget, because that would fault the editor thread.
			if (vertexBytes > kMaxNavGeometryBytes || triangleBytes > kMaxNavGeometryBytes) return false;
			if (!Readable(vertices, vertexBytes) || !Readable(elements, triangleBytes)) return false;

			// Constraint: the save read-back compares real geometry, so both arrays are digested here, over the
			// same bytes the file stores: every vertex, then the three indices of every triangle.
			out.verticesHash = navmeshdigest::VertexHash(vertices, vertexBytes);
			out.trianglesHash = navmeshdigest::TriangleHash(elements, triangleCount, kNavTriangleStride);
			out.hashed = true;

			double total = 0;
			std::uint32_t counted = 0;

			for (std::uint32_t t = 0; t < triangleCount; ++t)
			{
				const std::uint16_t* indices = reinterpret_cast<const std::uint16_t*>(elements + static_cast<std::size_t>(t) * kNavTriangleStride);
				const std::uint32_t a = indices[0], b = indices[1], c = indices[2];
				// Constraint: an index outside the vertex array must be skipped, not read.
				if (a >= vertexCount || b >= vertexCount || c >= vertexCount) continue;
				if (a == b || b == c || a == c) continue;

				const float* pa = reinterpret_cast<const float*>(vertices + static_cast<std::size_t>(a) * kNavVertexStride);
				const float* pb = reinterpret_cast<const float*>(vertices + static_cast<std::size_t>(b) * kNavVertexStride);
				const float* pc = reinterpret_cast<const float*>(vertices + static_cast<std::size_t>(c) * kNavVertexStride);

				const double ux = static_cast<double>(pb[0]) - pa[0];
				const double uy = static_cast<double>(pb[1]) - pa[1];
				const double uz = static_cast<double>(pb[2]) - pa[2];
				const double vx = static_cast<double>(pc[0]) - pa[0];
				const double vy = static_cast<double>(pc[1]) - pa[1];
				const double vz = static_cast<double>(pc[2]) - pa[2];

				const double nx = uy * vz - uz * vy;
				const double ny = uz * vx - ux * vz;
				const double nz = ux * vy - uy * vx;
				// Constraint: a degenerate triangle contributes no area, so its zero must not be counted as a measured one.
				const double magnitude = std::sqrt(nx * nx + ny * ny + nz * nz);
				if (!(magnitude > 0.0)) continue;

				total += magnitude / 2.0;
				++counted;

				// Constraint: cover is counted only on the triangles the area walk accepted, so the two stay comparable.
				const std::uint8_t* element = elements + static_cast<std::size_t>(t) * kNavTriangleStride;
				const std::uint32_t flags = *reinterpret_cast<const std::uint32_t*>(element + kNavFlagsOffset);
				const std::uint32_t cover = (flags >> kNavCoverEdgeShift) & kNavCoverEdgeMask;

				if (cover)
				{
					++out.cover.any;
					if (cover == kNavCoverEdgeMask) ++out.cover.all;
				}

				if (cover & 0x1) ++out.cover.edge01;
				if (cover & 0x2) ++out.cover.edge12;
				if (cover & 0x4) ++out.cover.edge20;

				const float height = *reinterpret_cast<const float*>(element + kNavCoverHeightOffset);
				if (height == height && height != FLT_MAX) ++out.cover.heightSet;

				const std::uint16_t* links = reinterpret_cast<const std::uint16_t*>(element + kNavLinksOffset);
				for (int edge = 0; edge < 3; ++edge)
					if ((cover & (1u << edge)) && links[edge] == 0xFFFF) ++out.cover.openEdges;
			}

			out.known = true;
			out.vertices = vertexCount;
			out.triangles = triangleCount;
			out.area = total;
			out.areaTriangles = counted;
			return true;
		}

		std::string CellJson(EditorCell* cell)
		{
			std::string grid = "null";
			if (!cell->HasInterior())
			{
				char buf[48];
				std::snprintf(buf, sizeof buf, "[%d,%d]",
					static_cast<int>(cell->GetGridX()), static_cast<int>(cell->GetGridY()));
				grid = buf;
			}

			const char* editorId = cell->GetFormEditorID();

			return "{\"editorId\":" + Quote(editorId ? editorId : "") +
				",\"formId\":" + Quote(HexId(cell->GetFormID())) +
				",\"interior\":" + (cell->HasInterior() ? "true" : "false") +
				",\"grid\":" + grid +
				",\"references\":" + std::to_string(cell->GetItemCount()) +
				",\"landscape\":" + (cell->GetLandspace() ? "true" : "false") + "}";
		}

		// Constraint: IsNavMesh must be checked before the count, because GetNavMeshesCount dereferences the array.
		std::string NavFormsJson(EditorCell* cell, std::uint32_t& total, std::uint32_t& navmeshCount,
			std::uint32_t& mismatched, std::uint32_t& vertices, std::uint32_t& triangles,
			std::uint32_t& readForms, std::uint32_t& emptyForms, std::uint32_t& unreadableForms,
			double& area, std::uint32_t& areaTriangles, NavCover& cover, bool& areaOk, bool& truncated)
		{
			std::string out;
			total = 0;
			navmeshCount = 0;
			mismatched = 0;
			vertices = 0;
			triangles = 0;
			readForms = 0;
			emptyForms = 0;
			unreadableForms = 0;
			area = 0;
			areaTriangles = 0;
			cover = NavCover();
			areaOk = false;
			truncated = false;
			bool areaComplete = true;

			if (!cell->IsNavMesh()) return out;

			total = cell->GetNavMeshesCount();
			EditorForm** list = cell->GetNavMeshes();
			// Constraint: a listing must not walk a null array pointer.
			if (!list) return out;

			const std::uint32_t capped = total > kMaxNavForms ? kMaxNavForms : total;
			truncated = total > capped;

			for (std::uint32_t i = 0; i < capped; ++i)
			{
				EditorForm* form = list[i];
				// Constraint: every entry must be readable before any of its members is used.
				if (!form || !Readable(form, sizeof(EditorForm))) continue;

				NavFormState state;
				state.type = static_cast<int>(form->GetFormType());
				state.formId = HexId(form->GetFormID());
				const char* editorId = form->GetFormEditorID();
				state.editorId = editorId ? editorId : "";
				state.navmesh = form->GetFormType() == EditorForm::FormType::ftNavMesh;

				if (state.navmesh)
				{
					auto* navmesh = reinterpret_cast<EditorNavMesh*>(form);
					state.navmeshId = static_cast<std::uint32_t>(navmesh->QNavmeshID());

					EditorCell* parent = navmesh->GetSaveParentCell();
					state.parentMatches = parent == cell;
					// Constraint: a parent pointer must be readable before its editor id is read.
					if (parent && Readable(parent, kCellReadable) && parent->GetFormEditorID())
						state.parentCell = parent->GetFormEditorID();

					NavGeometry geometry;
					if (ReadNavGeometry(form, geometry))
					{
						state.counts = true;
						state.vertices = geometry.vertices;
						state.triangles = geometry.triangles;
						state.area = geometry.area;
						state.areaTriangles = geometry.areaTriangles;
						state.areaOk = true;
						state.cover = geometry.cover;
						state.verticesHash = geometry.verticesHash;
						state.trianglesHash = geometry.trianglesHash;
						state.hashed = geometry.hashed;
						// Constraint: an emptied form measured no triangles, so its cover is unknown rather than zero.
						state.coverOk = !geometry.empty;
						state.geometry = geometry.empty ? GeometryName(NavArrayRead::Empty) : GeometryName(NavArrayRead::Present);

						vertices += geometry.vertices;
						triangles += geometry.triangles;
						area += geometry.area;
						areaTriangles += geometry.areaTriangles;

						cover.edge01 += geometry.cover.edge01;
						cover.edge12 += geometry.cover.edge12;
						cover.edge20 += geometry.cover.edge20;
						cover.any += geometry.cover.any;
						cover.all += geometry.cover.all;
						cover.openEdges += geometry.cover.openEdges;
						cover.heightSet += geometry.cover.heightSet;

						if (geometry.empty) ++emptyForms;
						else ++readForms;
					}
					else
					{
						// Constraint: a total is only offered when every form contributed one, so one unreadable form voids it.
						++unreadableForms;
						areaComplete = false;
					}

					++navmeshCount;
					if (!state.parentMatches) ++mismatched;
				}

				if (!out.empty()) out += ",";
				out += NavFormJson(state);
			}

			areaOk = navmeshCount > 0 && unreadableForms == 0 && areaComplete;
			return out;
		}

		// Constraint: before and after must come from one read path, or the change test compares two different things.
		bool ReadTotals(const void* cellPointer, std::uint32_t& forms, std::uint32_t& vertices, std::uint32_t& triangles)
		{
			forms = 0;
			vertices = 0;
			triangles = 0;

			if (!cellPointer || !Readable(cellPointer, kCellReadable)) return false;

			auto* cell = const_cast<EditorCell*>(static_cast<const EditorCell*>(cellPointer));

			std::uint32_t total = 0, mismatched = 0, readForms = 0, emptyForms = 0, unreadableForms = 0, areaTriangles = 0;
			double area = 0;
			NavCover cover;
			bool areaOk = false, truncated = false;
			NavFormsJson(cell, total, forms, mismatched, vertices, triangles, readForms, emptyForms, unreadableForms,
				area, areaTriangles, cover, areaOk, truncated);
			return true;
		}

		std::string ReadTotalsJson(const std::uint32_t forms, const std::uint32_t vertices, const std::uint32_t triangles, const bool known)
		{
			if (!known) return "null";
			return "{\"forms\":" + std::to_string(forms) +
				",\"vertices\":" + std::to_string(vertices) +
				",\"triangles\":" + std::to_string(triangles) + "}";
		}

		// Constraint: the loaded cell pointer must be readable, because the render window can outlive a cell.
		bool ReadCellState(const void* cellPointer, std::string& payload, std::string& errorCode, std::string& errorMessage)
		{
			if (!cellPointer || !Readable(cellPointer, kCellReadable))
			{
				errorCode = "no_cell";
				errorMessage = "no loaded cell is readable through the render window; open a cell first";
				return false;
			}

			auto* cell = const_cast<EditorCell*>(static_cast<const EditorCell*>(cellPointer));

			std::uint32_t total = 0, navmeshCount = 0, mismatched = 0, vertices = 0, triangles = 0;
			std::uint32_t readForms = 0, emptyForms = 0, unreadableForms = 0, areaTriangles = 0;
			double area = 0;
			NavCover cover;
			bool areaOk = false, truncated = false;
			const std::string forms = NavFormsJson(cell, total, navmeshCount, mismatched, vertices, triangles,
				readForms, emptyForms, unreadableForms, area, areaTriangles, cover, areaOk, truncated);

			// Constraint: presence is the cell's own array, coverage is what the array actually carries.
			const bool present = cell->IsNavMesh();

			payload = "{\"cell\":" + CellJson(cell) +
				",\"navmesh\":{\"present\":" + (present ? "true" : "false") +
				",\"count\":" + std::to_string(total) +
				",\"navmeshForms\":" + std::to_string(navmeshCount) +
				",\"parentMismatch\":" + std::to_string(mismatched) +
				",\"truncated\":" + (truncated ? "true" : "false") +
				",\"forms\":[" + forms + "]" +
				",\"coverage\":{\"covered\":" + (navmeshCount > 0 ? "true" : "false") +
				",\"forms\":" + std::to_string(navmeshCount) +
				",\"parentMismatch\":" + std::to_string(mismatched) +
				",\"readForms\":" + std::to_string(readForms) +
				",\"emptyForms\":" + std::to_string(emptyForms) +
				",\"unreadableForms\":" + std::to_string(unreadableForms) +
				",\"vertices\":" + std::to_string(vertices) +
				",\"triangles\":" + std::to_string(triangles) +
				",\"area\":" + (areaOk ? JsonNumber(area) : std::string("null")) +
				",\"areaTriangles\":" + (areaOk ? std::to_string(areaTriangles) : std::string("null")) +
				",\"edgeCover\":" + (areaOk ? CoverJson(cover) : std::string("null")) +
				",\"geometry\":\"form_arrays\"" +
				"}}}";
			return true;
		}

		// Constraint: generation needs a cell with no geometry, so the two counts both the reader and the editor consult are zeroed.
		bool StripCellGeometry(const void* cell, std::string& payload, std::string& errorCode, std::string& errorMessage)
		{
			auto* target = static_cast<EditorCell*>(const_cast<void*>(cell));
			if (!target || !Readable(target, kCellReadable))
			{
				errorCode = "no_cell";
				errorMessage = "no cell is loaded, so there is nothing to strip";
				return false;
			}

			std::uint32_t stripped = 0;
			std::uint32_t vertices = 0;
			// Constraint: repeated listings show one navmesh twice, so a form already zeroed is not counted again.
			std::vector<EditorForm*> seen;

			if (target->IsNavMesh())
			{
				EditorForm** list = target->GetNavMeshes();
				const std::uint32_t total = list ? target->GetNavMeshesCount() : 0;
				const std::uint32_t capped = total > kMaxNavForms ? kMaxNavForms : total;

				for (std::uint32_t i = 0; i < capped; ++i)
				{
					EditorForm* form = list[i];
					if (!form || !Readable(form, kNavReadable)) continue;
					if (form->GetFormType() != EditorForm::FormType::ftNavMesh) continue;
					bool already = false;
					for (EditorForm* known : seen) if (known == form) already = true;
					if (already) continue;
					seen.push_back(form);

					std::uint32_t before = 0;
					ReadNavArraySize(form, kNavVerticesOffset, before);
					auto* base = reinterpret_cast<std::uint8_t*>(form);
					*reinterpret_cast<std::uint32_t*>(base + kNavVerticesOffset + kNavArraySizeOffset) = 0;
					*reinterpret_cast<std::uint32_t*>(base + kNavTrianglesOffset + kNavArraySizeOffset) = 0;
					vertices += before;
					++stripped;
				}
			}

			payload = std::string("{\"stripped\":") + std::to_string(stripped) +
				",\"verticesCleared\":" + std::to_string(vertices) + "}";
			return true;
		}

		// Constraint: a Recast pass leaves the form it replaced in the cell's list with no geometry, so the list is
		// shortened by id instead of keeping one idle placeholder per apply. A form still carrying geometry is refused.
		bool PruneCellForms(const void* cell, const std::string& ids, std::string& payload,
			std::string& errorCode, std::string& errorMessage)
		{
			auto* target = static_cast<EditorCell*>(const_cast<void*>(cell));
			if (!target || !Readable(target, kCellReadable) || !target->IsNavMesh())
			{
				errorCode = "no_cell";
				errorMessage = "no cell with a navmesh list is loaded, so there is no form to drop";
				return false;
			}

			std::vector<std::uint32_t> wanted;
			std::uint32_t value = 0;
			int digits = 0;

			for (char raw : ids)
			{
				const char c = raw;
				const bool separator = c == ',';

				if (!separator && !std::isspace(static_cast<unsigned char>(c)))
				{
					int digit = -1;
					if (c >= '0' && c <= '9') digit = c - '0';
					else if (c >= 'a' && c <= 'f') digit = c - 'a' + 10;
					else if (c >= 'A' && c <= 'F') digit = c - 'A' + 10;

					if (digit < 0 || digits == 8)
					{
						errorCode = "invalid_argument";
						errorMessage = "nav.prune takes form ids of at most eight hex digits, separated by commas";
						return false;
					}

					value = (value << 4) | static_cast<std::uint32_t>(digit);
					++digits;
					continue;
				}

				if (digits)
				{
					wanted.push_back(value);
					value = 0;
					digits = 0;
				}
			}

			if (digits) wanted.push_back(value);

			if (wanted.empty())
			{
				errorCode = "invalid_argument";
				errorMessage = "nav.prune needs the form ids to drop, each as an eight digit hex id, separated by commas";
				return false;
			}

			EditorForm** list = target->GetNavMeshes();
			const std::uint32_t total = list ? target->GetNavMeshesCount() : 0;

			if (!list || total == 0)
			{
				errorCode = "no_navmesh";
				errorMessage = "the loaded cell lists no navmesh forms, so there is nothing to drop";
				return false;
			}

			// Constraint: the size is only written through the array the accessor reads, so a layout change fails the
			// call instead of shortening some other structure.
			auto* array = *reinterpret_cast<std::uint8_t**>(reinterpret_cast<std::uint8_t*>(target) + kCellNavMeshesOffset);
			if (!array || !Readable(array, kNavArraySizeOffset + sizeof(std::uint32_t)) ||
				*reinterpret_cast<EditorForm***>(array) != list ||
				*reinterpret_cast<std::uint32_t*>(array + kNavArraySizeOffset) != total)
			{
				errorCode = "navmesh_array_unreadable";
				errorMessage = "the loaded cell's navmesh list is not the array this build reads, so no form was dropped";
				return false;
			}

			std::string dropped;
			std::string keptLive;
			std::uint32_t keep = 0;

			for (std::uint32_t i = 0; i < total; ++i)
			{
				EditorForm* form = list[i];
				bool remove = false;

				if (form && Readable(form, kNavReadable))
				{
					const std::uint32_t id = form->GetFormID();
					bool selected = false;
					for (std::uint32_t candidate : wanted) if (candidate == id) selected = true;

					if (selected)
					{
						std::uint32_t vertices = 0;
						std::uint32_t triangles = 0;
						ReadNavArraySize(form, kNavVerticesOffset, vertices);
						ReadNavArraySize(form, kNavTrianglesOffset, triangles);

						// Constraint: a form that still carries geometry is the cell's mesh, so naming it is refused, not obeyed.
						if (vertices || triangles)
						{
							if (!keptLive.empty()) keptLive += ",";
							keptLive += Quote(HexId(id));
						}
						else
						{
							remove = true;
							if (!dropped.empty()) dropped += ",";
							dropped += Quote(HexId(id));
						}
					}
				}

				if (remove) continue;
				if (keep != i) list[keep] = form;
				++keep;
			}

			if (keep != total)
			{
				for (std::uint32_t i = keep; i < total; ++i) list[i] = nullptr;
				*reinterpret_cast<std::uint32_t*>(array + kNavArraySizeOffset) = keep;
			}

			payload = "{\"pruned\":[" + dropped + "],\"keptLive\":[" + keptLive +
				"],\"forms\":" + std::to_string(keep) + "}";
			return true;
		}
	}
}
