#include "termlink_direct_ffi_v4.h"

int termlink_direct_ffi_v4_header_layout_test(void) {
  return (int)(sizeof(TermLinkDirectCreateV4) +
               sizeof(TermLinkSocketAddrV4) +
               sizeof(TermLinkDirectGatherV4) +
               sizeof(TermLinkDirectCandidateV4) +
               sizeof(TermLinkDirectCandidateBatchV4) +
               sizeof(TermLinkDirectCandidatePairV4) +
               sizeof(TermLinkDirectBeginChecksV4) +
               sizeof(TermLinkDirectAppendCheckPairsV4) +
               sizeof(TermLinkDirectCommitNominationV4) +
               sizeof(TermLinkDirectControlV4) +
               sizeof(TermLinkDirectSignatureV4) +
               sizeof(TermLinkDirectOpaqueBytesV4) +
               sizeof(TermLinkDirectPairValidatedV4) +
               sizeof(TermLinkDirectChecksCompleteV4) +
               sizeof(TermLinkDirectNominationReadyV4) +
               sizeof(TermLinkDirectShadowReadyV4) +
               sizeof(TermLinkDirectEventPayloadV4) +
               sizeof(TermLinkDirectEventV4));
}
