#pragma once

#include <cstdint>
#include <string>

namespace ckagent
{
	// Constraint: the pipeline measures a cell through this reader, so its entries are declared once here.
	namespace navmeshread
	{
		bool ReadCellState(const void* cell, std::string& payload, std::string& errorCode, std::string& errorMessage);
		bool StripCellGeometry(const void* cell, std::string& payload, std::string& errorCode, std::string& errorMessage);
		bool PruneCellForms(const void* cell, const std::string& ids, std::string& payload,
			std::string& errorCode, std::string& errorMessage);
		bool ReadTotals(const void* cell, std::uint32_t& forms, std::uint32_t& vertices, std::uint32_t& triangles);
		std::string ReadTotalsJson(const std::uint32_t forms, const std::uint32_t vertices, const std::uint32_t triangles, const bool known);
	}
}
