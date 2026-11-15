#include "stdafx.h"
#ifdef _WIN32
#ifdef _WIN32
#include "resource.h"
#else
#include <resource.h>
#endif
#endif

#define MAX_LEVEL	11		// 1 << 11 == 2048

#define TBL_SIZE (2048+1024+512+256+128+64+32+16+8+4+2)

short SineTable[TBL_SIZE];
short SawtoothTable[TBL_SIZE];
short PulseTable[TBL_SIZE];
short TriangleTable[TBL_SIZE];
short NoiseTable[TBL_SIZE];
short Saw303Table[TBL_SIZE];

short const *OscTableOffsets[] = { SineTable, SawtoothTable, PulseTable, TriangleTable, NoiseTable, Saw303Table };


#ifndef _WIN32
// Fork (R1): the oscillator tables are a resource of the Windows exe; the portable build embeds the same file
// (res/osctbl.raw, as a C array that CMake writes).
extern const unsigned char OscTblRaw[];
extern const unsigned int OscTblRawSize;
#endif

void InitOscTables()
{
#ifdef _WIN32
	HRSRC hr = FindResource(NULL, (char const *)IDR_OSCTBL, "osctbl");
	assert(hr != NULL);
	
	HGLOBAL hg = LoadResource(NULL, hr);
	assert(hg != NULL);

	short *pdata = (short *)LockResource(hg);
	assert(pdata != NULL);
#else
	assert(OscTblRawSize >= 2 * 5 * TBL_SIZE);
	short *pdata = (short *)OscTblRaw;
#endif

	memcpy(SineTable, pdata, 2*TBL_SIZE);
	memcpy(SawtoothTable, pdata+TBL_SIZE, 2*TBL_SIZE);
	memcpy(PulseTable, pdata+2*TBL_SIZE, 2*TBL_SIZE);
	memcpy(TriangleTable, pdata+3*TBL_SIZE, 2*TBL_SIZE);	
	memcpy(Saw303Table, pdata+4*TBL_SIZE, 2*TBL_SIZE);	
	
	srand(0);

	for (int c = 0; c < TBL_SIZE; c++)
	{
		NoiseTable[c] = (rand() | (rand() & 1) * 32768) - 32768;
	}

}