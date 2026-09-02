#include "termlink_direct_ffi_v3.h"

int termlink_direct_ffi_v3_header_layout_test(void) {
  return (int)(sizeof(TermLinkDirectCreateV3) +
               sizeof(TermLinkDirectGatherV3) +
               sizeof(TermLinkDirectNominateV3) +
               sizeof(TermLinkDirectCandidatesV3));
}
