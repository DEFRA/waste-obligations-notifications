#!/bin/sh
set -eu

topic_name="waste_obligations_analytics_events"
queue_name="waste_obligations_notifications_analytics_events_queue"

topic_arn="$(aws sns create-topic --name "$topic_name" --query TopicArn --output text)"
queue_url="$(aws sqs create-queue --queue-name "$queue_name" --query QueueUrl --output text)"
queue_arn="$(
    aws sqs get-queue-attributes \
        --queue-url "$queue_url" \
        --attribute-names QueueArn \
        --query 'Attributes.QueueArn' \
        --output text
)"

cat > /tmp/queue-attributes.json <<EOF
{
  "Policy": "{\"Version\":\"2012-10-17\",\"Statement\":[{\"Effect\":\"Allow\",\"Principal\":\"*\",\"Action\":\"sqs:SendMessage\",\"Resource\":\"$queue_arn\",\"Condition\":{\"ArnEquals\":{\"aws:SourceArn\":\"$topic_arn\"}}}]}"
}
EOF

aws sqs set-queue-attributes --queue-url "$queue_url" --attributes file:///tmp/queue-attributes.json
aws sns subscribe \
    --topic-arn "$topic_arn" \
    --protocol sqs \
    --notification-endpoint "$queue_arn" \
    --attributes RawMessageDelivery=true > /dev/null

echo "Ready: $queue_name is subscribed to $topic_name"

touch /tmp/floci-ready
exec tail -f /dev/null
