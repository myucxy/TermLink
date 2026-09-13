#include "termlink_direct_ffi_v5.h"

/*
 * This translation unit is intentionally link-free.  The header's static
 * assertions validate the ABI on every supported C compiler; keeping a
 * callable symbol makes it convenient for CI smoke-compilation on MSVC,
 * clang, and GCC.
 */
int termlink_direct_ffi_v5_header_layout_test(void) {
  return (int)(sizeof(TermLinkDirectPathV5) +
               sizeof(TermLinkDirectCreateV5) +
               sizeof(TermLinkDirectEventV5) +
               sizeof(TermLinkSocketAddrV5) +
               sizeof(TermLinkDirectCandidateV5) +
               sizeof(TermLinkDirectCandidatePairV5) +
               sizeof(TermLinkDirectPromoteV5) +
               sizeof(TermLinkDirectPathInfoV5) +
               sizeof(TermLinkDirectSignatureV5) +
               sizeof(TermLinkDirectSessionBarrierV5) +
               sizeof(TermLinkDirectProposalV5) +
               sizeof(TermLinkDirectTransitionV5) +
               sizeof(TermLinkDirectCursorV5) +
               sizeof(TermLinkDirectRouteEnvelopeV5) +
               sizeof(TermLinkDirectRouteActionV5) +
               sizeof(TermLinkDirectIngressV5));
}
