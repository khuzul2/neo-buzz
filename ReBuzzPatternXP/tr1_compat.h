// Fork addition: PatternXPRE's stdafx.h says "using namespace std::tr1;".
// MSVC's STL no longer declares std::tr1, so that line fails to compile with
// current toolsets. Everything PatternXP used from tr1 (shared_ptr, ...) is in
// std since C++11, so an empty namespace is enough. Force-included by
// ReBuzzPatternXP.vcxproj so the PatternXPRE submodule stays untouched.
#pragma once
namespace std { namespace tr1 {} }
