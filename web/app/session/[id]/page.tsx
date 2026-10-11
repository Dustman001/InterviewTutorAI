import { Suspense } from "react";
import LiveSession from "@/components/LiveSession";

export default function SessionPage({ params }: PageProps<"/session/[id]">) {
  return <Suspense fallback={null}>{params.then(({ id }) => <LiveSession id={id} />)}</Suspense>;
}
