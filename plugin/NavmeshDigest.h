#pragma once

#include <cstddef>
#include <cstdint>
#include <cstdio>
#include <string>

namespace ckagent
{
	// Constraint: the saved plugin is compared against the geometry this process holds, so both sides have to
	// hash the same bytes the same way. FNV-1a 64 over little-endian arrays is the definition below and in
	// NavmeshPersist.cs; the static_assert pins it, so a drift fails this compile instead of a save verdict.
	namespace navmeshdigest
	{
		constexpr std::uint64_t kOffsetBasis = 0xCBF29CE484222325ull;
		constexpr std::uint64_t kPrime = 0x100000001B3ull;

		constexpr std::uint64_t Fnv1a64(const std::byte* data, std::size_t size)
		{
			std::uint64_t hash = kOffsetBasis;
			for (std::size_t i = 0; i < size; ++i)
			{
				hash ^= std::to_integer<std::uint64_t>(data[i]);
				hash *= kPrime;
			}

			return hash;
		}

		constexpr std::uint64_t Fnv1a64(const char* text, std::size_t size)
		{
			std::uint64_t hash = kOffsetBasis;
			for (std::size_t i = 0; i < size; ++i)
			{
				hash ^= static_cast<std::uint64_t>(static_cast<unsigned char>(text[i]));
				hash *= kPrime;
			}

			return hash;
		}

		// Invariant: the value every read-back is compared against, checked against the published FNV-1a vector.
		static_assert(Fnv1a64("abc", 3) == 0xE71FA2190541574Bull,
			"FNV-1a 64 over 'abc' must be e71fa2190541574b, the digest the server recomputes");

		inline std::string Hex(std::uint64_t value)
		{
			char buf[17];
			std::snprintf(buf, sizeof buf, "%016llx", static_cast<unsigned long long>(value));
			return buf;
		}

		// The vertex array is hashed as it lies in memory: three little-endian f32 values per vertex.
		inline std::string VertexHash(const void* vertices, std::size_t bytes)
		{
			return Hex(Fnv1a64(static_cast<const std::byte*>(vertices), bytes));
		}

		// The triangle hash covers only the three u16 vertex indices of each triangle, which is the part the
		// file stores identically; the in-memory element is wider, so the stride is the caller's.
		inline std::string TriangleHash(const std::uint8_t* elements, std::uint32_t count, std::size_t stride)
		{
			std::uint64_t hash = kOffsetBasis;
			for (std::uint32_t t = 0; t < count; ++t)
			{
				const std::byte* indices = reinterpret_cast<const std::byte*>(elements + static_cast<std::size_t>(t) * stride);
				for (std::size_t i = 0; i < 6; ++i)
				{
					hash ^= std::to_integer<std::uint64_t>(indices[i]);
					hash *= kPrime;
				}
			}

			return Hex(hash);
		}
	}
}
